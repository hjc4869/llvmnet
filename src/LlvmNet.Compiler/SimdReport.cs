using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using LlvmNet.Runtime;

namespace LlvmNet;

/// <summary>
/// --report-simd (SV-10): what a program's vector code runs as, read from the emitted image's IL as --diagnose-limits
/// reads it. Per function it counts the Vector128 and Vector256 operations, which RyuJIT and ILC compile to the host's
/// instructions, and the calls of the runtime's SIMD helpers (<see cref="X86"/>, <see cref="Vectors"/>,
/// <see cref="Simd128"/>); each helper is classed by the instruction sets whose IsSupported its own IL tests, for x64 and
/// for ARM64, and as composed where it tests none (Vector128 operations, or lane by lane). The functions the lowering
/// scalarized and the reductions it recognized come from <see cref="VectorEmitter"/>. Emscripten's
/// WASM_SIMD_COMPAT_SLOW warnings are the model: the report names the operations that are not one instruction.
/// </summary>
internal static class SimdReport
{
    private static readonly Dictionary<ushort, OperandType> OperandTypes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => (ushort)code.Value, code => code.OperandType);
    private static readonly Type[] HelperTypes = [typeof(X86), typeof(Vectors), typeof(Simd128)];
    /// <summary>The instruction sets of the BitOperations methods that are a helper's whole operation (the crc32
    /// helpers'). CoreLib is built per architecture and its IL for x64 has the ARM64 branches removed, so they are not
    /// read from it; the bit counts some compositions end with are left out.</summary>
    private static readonly Dictionary<string, (string X64, string Arm64)> BitInstructions = new()
    {
        [nameof(BitOperations.Crc32C)] = ("Sse42", "Crc32"),
    };

    internal static void Write(byte[] image, IReadOnlyCollection<(string Function, string? Reason)> scalarized,
        IReadOnlyCollection<(string Function, uint Differences, uint Products)> reductions, int fragmentBits, TextWriter output)
    {
        using var stream = new MemoryStream(image, false);
        using var reader = new PEReader(stream);
        MetadataReader metadata = reader.GetMetadataReader();
        var functions = new List<(string Name, int Operations, int Calls)>();
        var helpers = new Dictionary<(Type Owner, string Name), (int Calls, SortedSet<string> Functions)>();
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            string name = metadata.GetString(method.Name);
            int operations = 0, calls = 0;
            foreach (int token in CallTokens(reader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? []))
            {
                if (Callee(metadata, token) is not (string owner, string member)) continue;
                // Vectors' loads and stores of partial vectors and masks are memory accesses, not compositions.
                if (owner.StartsWith("System.Runtime.Intrinsics.Vector", StringComparison.Ordinal) ||
                    owner == typeof(Vectors).FullName && (member.StartsWith("Load", StringComparison.Ordinal) || member.StartsWith("Store", StringComparison.Ordinal)))
                    operations++;
                else if (HelperTypes.FirstOrDefault(type => type.FullName == owner) is Type helper)
                {
                    calls++;
                    (int count, SortedSet<string> callers) = helpers.TryGetValue((helper, member), out var entry) ? entry : (0, new SortedSet<string>(StringComparer.Ordinal));
                    callers.Add(Llvm.Demangle(name));
                    helpers[(helper, member)] = (count + 1, callers);
                }
            }
            if (operations + calls != 0) functions.Add((Llvm.Demangle(name), operations, calls));
        }
        output.WriteLine($"llvmnet: simd: {functions.Count} functions make {functions.Sum(function => function.Operations)} Vector{fragmentBits} operations and " +
            $"{functions.Sum(function => function.Calls)} SIMD helper calls; {scalarized.Count} functions with vector IR run as scalar code");
        foreach ((string function, string? reason) in scalarized.OrderBy(entry => entry.Function, StringComparer.Ordinal))
            output.WriteLine($"llvmnet: simd: scalarized {Llvm.Demangle(function)} ({reason ?? "its vector code moves more lanes than it computes on"})");
        foreach ((string function, uint differences, uint products) in reductions.OrderBy(entry => entry.Function, StringComparer.Ordinal))
            output.WriteLine($"llvmnet: simd: reductions of {Llvm.Demangle(function)}: {VectorEmitter.Describe(differences, products)}");
        if (helpers.Count != 0)
        {
            output.WriteLine($"llvmnet: simd: {"calls",7} {"x64",-22} {"ARM64",-22} helper: functions");
            foreach (((Type owner, string member), (int calls, SortedSet<string> callers)) in helpers.OrderByDescending(entry => entry.Value.Calls).ThenBy(entry => entry.Key.Name, StringComparer.Ordinal))
            {
                (string x64, string arm64) = InstructionSets(owner, member);
                string intrinsic = owner.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name == member)
                    .SelectMany(method => method.GetCustomAttributes<LlvmIntrinsicAttribute>()).Select(attribute => attribute.Name).FirstOrDefault() is string llvm ? $" ({llvm})" : "";
                output.WriteLine($"llvmnet: simd: {calls,7} {x64,-22} {arm64,-22} {owner.Name}.{member}{intrinsic}: {string.Join(", ", callers.Take(4))}{(callers.Count > 4 ? $" (+{callers.Count - 4})" : "")}");
            }
        }
        if (functions.Count == 0) return;
        output.WriteLine($"llvmnet: simd: {"operations",10} {"calls",6} function");
        foreach (var function in functions.OrderByDescending(function => function.Operations + function.Calls).ThenBy(function => function.Name, StringComparer.Ordinal).Take(20))
            output.WriteLine($"llvmnet: simd: {function.Operations,10} {function.Calls,6} {function.Name}");
    }

    /// <summary>The metadata tokens of the methods the IL calls (call, callvirt, newobj, ldftn).</summary>
    private static List<int> CallTokens(byte[] il)
    {
        var tokens = new List<int>();
        for (int offset = 0; offset < il.Length;)
        {
            ushort value = il[offset++];
            if (value == 0xFE) value = (ushort)(0xFE00 | il[offset++]);
            switch (OperandTypes[value])
            {
                case OperandType.InlineMethod:
                    if (value is 0x28 or 0x6F or 0x73 or 0xFE06) tokens.Add(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset)));
                    offset += 4;
                    break;
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar: offset += 1; break;
                case OperandType.InlineVar: offset += 2; break;
                case OperandType.InlineI8 or OperandType.InlineR: offset += 8; break;
                case OperandType.InlineSwitch: offset += 4 + BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset)) * 4; break;
                default: offset += 4; break;
            }
        }
        return tokens;
    }

    /// <summary>The declaring type's full name and the name of a method the image references, or null for its own methods.</summary>
    private static (string Owner, string Name)? Callee(MetadataReader metadata, int token)
    {
        EntityHandle target = MetadataTokens.EntityHandle(token);
        if (target.Kind == HandleKind.MethodSpecification) target = metadata.GetMethodSpecification((MethodSpecificationHandle)target).Method;
        if (target.Kind != HandleKind.MemberReference) return null;
        MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)target);
        EntityHandle parent = member.Parent;
        if (parent.Kind == HandleKind.TypeSpecification)
        {
            BlobReader signature = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
            if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
            signature.ReadSignatureTypeCode();
            parent = signature.ReadTypeHandle();
        }
        if (parent.Kind != HandleKind.TypeReference) return null;
        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)parent);
        return (metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name), metadata.GetString(member.Name));
    }

    /// <summary>The x64 and the ARM64 instruction sets whose IsSupported a helper's IL tests, itself or in the runtime
    /// methods it calls (a 256-bit helper's 128-bit halves), with <see cref="BitInstructions"/> for BitOperations'
    /// CRC-32C; "composed" for a host family it tests none for.</summary>
    private static (string X64, string Arm64) InstructionSets(Type owner, string name)
    {
        SortedSet<string> x64 = new(StringComparer.Ordinal), arm64 = new(StringComparer.Ordinal);
        HashSet<MethodBase> visited = [];
        void Visit(MethodBase method)
        {
            if (!visited.Add(method) || method.GetMethodBody()?.GetILAsByteArray() is not byte[] bytes) return;
            Type[] typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : [];
            Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : [];
            foreach (int token in CallTokens(bytes))
            {
                MethodBase? callee;
                try { callee = method.Module.ResolveMethod(token, typeArguments, methodArguments); }
                catch (ArgumentException) { continue; }
                if (callee?.DeclaringType is not Type declaring) continue;
                string? space = (declaring.DeclaringType ?? declaring).Namespace;
                if (callee.Name == "get_IsSupported" && space == "System.Runtime.Intrinsics.X86") x64.Add(IsaName(declaring));
                else if (callee.Name == "get_IsSupported" && space == "System.Runtime.Intrinsics.Arm") arm64.Add(IsaName(declaring));
                else if (declaring == typeof(BitOperations) && BitInstructions.TryGetValue(callee.Name, out var bits))
                {
                    x64.Add(bits.X64);
                    arm64.Add(bits.Arm64);
                }
                else if (declaring.Assembly == typeof(X86).Assembly) Visit(callee);
            }
        }
        foreach (MethodInfo method in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(method => method.Name == name))
            Visit(method);
        return (x64.Count == 0 ? "composed" : string.Join("/", x64), arm64.Count == 0 ? "composed" : string.Join("/", arm64));
    }

    private static string IsaName(Type type) => type.DeclaringType is Type outer ? $"{outer.Name}.{type.Name}" : type.Name;
}
