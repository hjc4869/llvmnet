using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace LlvmNet;

/// <summary>
/// --diagnose-limits: per-method IL size, locals and maximum stack from the emitted image, joined with LLVM block
/// and instruction counts, flagging methods near the CLR local limit or beyond a threshold at which RyuJIT
/// compiles with minimal optimization (MinOpts).
/// </summary>
internal static class LimitsReport
{
    internal const int MinOptsIlBytes = 60000, MinOptsBlocks = 2000, MinOptsLocals = 2000, MinOptsInstructions = 20000, LocalLimit = 65535;

    internal static void Write(byte[] image, IReadOnlyDictionary<string, (int Blocks, int Instructions)> llvm, TextWriter output)
    {
        using var stream = new MemoryStream(image, false);
        using var reader = new PEReader(stream);
        MetadataReader metadata = reader.GetMetadataReader();
        var rows = new List<(string Name, int Il, int Locals, int Stack, int Blocks, int Instructions)>();
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            string name = metadata.GetString(method.Name);
            if (!llvm.TryGetValue(name, out var counts)) continue;
            MethodBodyBlock body = reader.GetMethodBody(method.RelativeVirtualAddress);
            int locals = 0;
            if (!body.LocalSignature.IsNil)
            {
                BlobReader signature = metadata.GetBlobReader(metadata.GetStandaloneSignature(body.LocalSignature).Signature);
                signature.ReadSignatureHeader();
                locals = signature.ReadCompressedInteger();
            }
            rows.Add((name, body.Size, locals, body.MaxStack, counts.Blocks, counts.Instructions));
        }
        var flagged = rows.Where(row => row.Il > MinOptsIlBytes || row.Blocks > MinOptsBlocks || row.Locals > MinOptsLocals || row.Instructions > MinOptsInstructions).ToList();
        output.WriteLine($"llvmnet: limits: {rows.Count} methods, {rows.Sum(row => (long)row.Il)} IL bytes; {flagged.Count} beyond a RyuJIT MinOpts threshold " +
            $"(IL>{MinOptsIlBytes}, blocks>{MinOptsBlocks}, locals>{MinOptsLocals}, LLVM instructions>{MinOptsInstructions}); CLR local limit {LocalLimit}.");
        output.WriteLine("llvmnet: limits: IL-bytes locals max-stack llvm-blocks llvm-instructions flags function");
        foreach (var row in rows.OrderByDescending(row => row.Il).Take(Math.Max(20, flagged.Count)))
        {
            string flags = string.Join(",", new[]
            {
                row.Il > MinOptsIlBytes ? "il" : null, row.Blocks > MinOptsBlocks ? "blocks" : null,
                row.Locals > MinOptsLocals ? "locals" : null, row.Instructions > MinOptsInstructions ? "instructions" : null,
                row.Locals > LocalLimit * 9 / 10 ? "NEAR-LOCAL-LIMIT" : null
            }.OfType<string>());
            output.WriteLine($"llvmnet: limits: {row.Il,9} {row.Locals,6} {row.Stack,9} {row.Blocks,11} {row.Instructions,17} {(flags.Length == 0 ? "-" : flags),-12} {Llvm.Demangle(row.Name)}");
        }
    }
}
