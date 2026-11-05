using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace LlvmNet;

/// <summary>
/// --diagnose-limits: RyuJIT's inputs to its MinOpts decision for each translated method, computed from the emitted IL
/// as the JIT's importer counts them (compSetOptimizationLevel in compiler.cpp, fgFindJumpTargets and
/// fgFindBasicBlocks in fgbasic.cpp): IL bytes, IL instructions without prefixes, basic blocks, locals with the
/// arguments, and local references (every ldarg, ldarga, starg, ldloc, ldloca and stloc). In release builds of the
/// runtime a method past any of the five thresholds is compiled with minimal optimization, and its tier-1
/// recompilation is discarded (JP-05, BE-46); AOT compilation never falls back. The instruction and reference counts
/// do not reach a tier-0 compilation that switches to full optimization, for a loop on-stack replacement cannot patch
/// (localloc) or a tail. call: fgSwitchToOptimized reinitializes the options after the counts are taken, so such a
/// method is optimized unless it is past the IL, block or local thresholds. Methods near the CLR's limit on locals are
/// flagged too.
/// </summary>
internal static class LimitsReport
{
    internal const int MinOptsIlBytes = 60000, MinOptsInstructions = 20000, MinOptsBlocks = 2000, MinOptsLocals = 2000, MinOptsReferences = 8000, LocalLimit = 65535;

    internal readonly record struct Counts(int IlBytes, int Instructions, int Blocks, int Locals, int References)
    {
        internal bool MinOpts => Always || Instructions > MinOptsInstructions || References > MinOptsReferences;

        /// <summary>Past a threshold that every compilation checks, a tier-0 one switched to full optimization included.</summary>
        internal bool Always => IlBytes > MinOptsIlBytes || Blocks > MinOptsBlocks || Locals > MinOptsLocals;

        internal string Flags => string.Join(",", new[]
        {
            IlBytes > MinOptsIlBytes ? "il" : null, Instructions > MinOptsInstructions ? "instructions" : null, Blocks > MinOptsBlocks ? "blocks" : null,
            Locals > MinOptsLocals ? "locals" : null, References > MinOptsReferences ? "references" : null, Locals > LocalLimit * 9 / 10 ? "NEAR-LOCAL-LIMIT" : null
        }.OfType<string>());
    }

    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes).GetFields()
        .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(opcode => unchecked((ushort)opcode.Value));

    /// <summary>The counts of every method with a body, by metadata token.</summary>
    internal static Dictionary<int, (string Name, Counts Counts)> Measure(PEReader reader)
    {
        MetadataReader metadata = reader.GetMetadataReader();
        var result = new Dictionary<int, (string, Counts)>();
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            MethodBodyBlock body = reader.GetMethodBody(method.RelativeVirtualAddress);
            BlobReader signature = metadata.GetBlobReader(method.Signature);
            SignatureHeader header = signature.ReadSignatureHeader();
            int locals = signature.ReadCompressedInteger() + (header.IsInstance ? 1 : 0);
            if (!body.LocalSignature.IsNil)
            {
                BlobReader localSignature = metadata.GetBlobReader(metadata.GetStandaloneSignature(body.LocalSignature).Signature);
                localSignature.ReadSignatureHeader();
                locals += localSignature.ReadCompressedInteger();
            }
            result[MetadataTokens.GetToken(handle)] = (metadata.GetString(method.Name), Count(body, locals));
        }
        return result;
    }

    private static Counts Count(MethodBodyBlock body, int locals)
    {
        BlobReader il = body.GetILReader();
        int size = il.Length, instructions = 0, references = 0;
        var starts = new HashSet<int> { 0 };
        while (il.RemainingBytes > 0)
        {
            ushort value = il.ReadByte();
            if (value == 0xFE) value = (ushort)(0xFE00 | il.ReadByte());
            OpCode opcode = OpCodesByValue[value];
            // ldarg.0 to stloc.s (0x02 to 0x13) and ldarg to stloc (0xFE09 to 0xFE0E), address forms included.
            if (value is >= 0x02 and <= 0x13 or >= 0xFE09 and <= 0xFE0E) references++;
            if (opcode.OpCodeType != OpCodeType.Prefix) instructions++;
            int branch = 0;
            switch (opcode.OperandType)
            {
                case OperandType.ShortInlineBrTarget: branch = il.ReadSByte(); break;
                case OperandType.InlineBrTarget: branch = il.ReadInt32(); break;
                case OperandType.InlineSwitch:
                    int targets = il.ReadInt32();
                    int next = il.Offset + 4 * targets;
                    for (int index = 0; index < targets; index++)
                        starts.Add(next + il.ReadInt32());
                    break;
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI or OperandType.ShortInlineVar: il.Offset += 1; break;
                case OperandType.InlineVar: il.Offset += 2; break;
                case OperandType.InlineI8 or OperandType.InlineR: il.Offset += 8; break;
                default: il.Offset += 4; break;
            }
            if (opcode.OperandType is OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget)
                starts.Add(il.Offset + branch);
            if (opcode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw ||
                opcode.OperandType == OperandType.InlineSwitch || value is 0xDC or 0xFE11)
                starts.Add(il.Offset);
        }
        foreach (ExceptionRegion region in body.ExceptionRegions)
        {
            starts.Add(region.TryOffset);
            starts.Add(region.TryOffset + region.TryLength);
            starts.Add(region.HandlerOffset);
            starts.Add(region.HandlerOffset + region.HandlerLength);
            if (region.Kind == ExceptionRegionKind.Filter) starts.Add(region.FilterOffset);
        }
        starts.RemoveWhere(start => start >= size);
        return new Counts(size, instructions, starts.Count, locals, references);
    }

    internal static void Write(byte[] image, IReadOnlyDictionary<string, int> llvmInstructions, TextWriter output)
    {
        using var stream = new MemoryStream(image, false);
        using var reader = new PEReader(stream);
        var rows = Measure(reader).Values.Where(row => llvmInstructions.ContainsKey(row.Name)).ToList();
        var flagged = rows.Where(row => row.Counts.MinOpts).ToList();
        output.WriteLine($"llvmnet: limits: {rows.Count} methods, {rows.Sum(row => (long)row.Counts.IlBytes)} IL bytes; {flagged.Count} beyond a RyuJIT MinOpts threshold " +
            $"(IL bytes>{MinOptsIlBytes}, instructions>{MinOptsInstructions}, blocks>{MinOptsBlocks}, locals>{MinOptsLocals}, local references>{MinOptsReferences}), " +
            $"{flagged.Count(row => row.Counts.Always)} of them past the IL, block or local threshold, which a tier-0 method switched to full optimization also checks; CLR local limit {LocalLimit}.");
        output.WriteLine("llvmnet: limits: IL-bytes instructions blocks locals references llvm-instructions flags function");
        // Every flagged method, then the largest others up to 20 rows.
        foreach (var (name, counts) in rows.OrderByDescending(row => row.Counts.MinOpts).ThenByDescending(row => row.Counts.IlBytes).Take(Math.Max(20, flagged.Count)))
        {
            string flags = counts.Flags;
            output.WriteLine($"llvmnet: limits: {counts.IlBytes,9} {counts.Instructions,12} {counts.Blocks,6} {counts.Locals,6} {counts.References,10} {llvmInstructions[name],17} " +
                $"{(flags.Length == 0 ? "-" : flags),-12} {Llvm.Demangle(name)}");
        }
    }
}
