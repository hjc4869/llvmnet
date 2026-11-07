using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

try
{
    if (args is ["simd128-probe", string inputPath])
    {
        Simd128Probe.Run(inputPath);
        return 0;
    }
    if (args is ["compare-numeric", string expectedPath, string actualPath, string toleranceText])
    {
        double tolerance = double.Parse(toleranceText, CultureInfo.InvariantCulture);
        if (tolerance < 0 || !double.IsFinite(tolerance)) throw new ArgumentException("Invalid comparison tolerance.");
        string expected = File.ReadAllText(expectedPath);
        string actual = File.ReadAllText(actualPath);
        var numbers = new Regex(@"(?<![\w.])[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eEdD][-+]?\d+)?(?![\w.])", RegexOptions.CultureInvariant);
        string Shape(string text) => Regex.Replace(numbers.Replace(text, "<number>"), @"\s+", " ").Trim();
        if (Shape(expected) != Shape(actual)) throw new InvalidOperationException("Non-numeric output text differs.");
        MatchCollection expectedNumbers = numbers.Matches(expected);
        MatchCollection actualNumbers = numbers.Matches(actual);
        if (expectedNumbers.Count != actualNumbers.Count) throw new InvalidOperationException("Numeric output field count differs.");
        for (int index = 0; index < expectedNumbers.Count; index++)
        {
            string first = expectedNumbers[index].Value;
            string second = actualNumbers[index].Value;
            if (first == second) continue;
            if (BigInteger.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger expectedInteger) &&
                BigInteger.TryParse(second, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger actualInteger))
            {
                if (expectedInteger == actualInteger) continue;
                throw new InvalidOperationException($"Integer field {index} differs: {first} != {second}");
            }
            double expectedValue = double.Parse(first.Replace('D', 'E').Replace('d', 'e'), CultureInfo.InvariantCulture);
            double actualValue = double.Parse(second.Replace('D', 'E').Replace('d', 'e'), CultureInfo.InvariantCulture);
            if (expectedValue != actualValue && (!double.IsFinite(expectedValue) || !double.IsFinite(actualValue) || Math.Abs(expectedValue - actualValue) > tolerance))
                throw new InvalidOperationException($"Numeric field {index} differs beyond {tolerance}: {first} != {second}");
        }
        Console.WriteLine($"PASS: {expectedNumbers.Count} numeric fields within absolute tolerance {tolerance.ToString(CultureInfo.InvariantCulture)}; other text matches.");
        return 0;
    }
    if (args is ["inspect-methods", string methodPath, string filter])
    {
        using FileStream stream = File.OpenRead(methodPath);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            string name = metadata.GetString(method.Name);
            if (method.RelativeVirtualAddress == 0 || !name.Contains(filter, StringComparison.Ordinal)) continue;
            MethodBodyBlock body = image.GetMethodBody(method.RelativeVirtualAddress);
            int locals = 0;
            if (!body.LocalSignature.IsNil)
            {
                BlobReader signature = metadata.GetBlobReader(metadata.GetStandaloneSignature(body.LocalSignature).Signature);
                signature.ReadSignatureHeader();
                locals = signature.ReadCompressedInteger();
            }
            Console.WriteLine($"{name}: locals={locals} maxstack={body.MaxStack} ilbytes={body.GetILContent().Length}");
        }
        return 0;
    }
    if (args is ["address-fields", string fieldPath])
    {
        // The fields of global and constant addresses per class, and how many of them are initonly (JP-04).
        using FileStream stream = File.OpenRead(fieldPath);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            int fields = 0, initOnly = 0;
            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                string name = metadata.GetString(field.Name);
                if (!name.StartsWith("__global", StringComparison.Ordinal) && !name.StartsWith("__constant", StringComparison.Ordinal)) continue;
                fields++;
                if (field.Attributes.HasFlag(FieldAttributes.InitOnly)) initOnly++;
            }
            if (fields > 0)
                Console.WriteLine($"{metadata.GetString(type.Name)}: {fields} address fields, {initOnly} initonly");
        }
        return 0;
    }
    if (args is ["il-opcodes", string opcodePath, string methodName, .. var flags] && flags is [] or ["--locals"])
    {
        // The CIL opcodes of each method named methodName, one line per method, for checks of what a lowering emits.
        // With --locals, every ldloc and stloc form is written ldloc:N or stloc:N with its local's index.
        bool withLocals = flags is ["--locals"];
        var opcodes = typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (System.Reflection.Emit.OpCode)field.GetValue(null)!).ToDictionary(opcode => (ushort)opcode.Value);
        using FileStream stream = File.OpenRead(opcodePath);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        int found = 0;
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0 || metadata.GetString(method.Name) != methodName) continue;
            BlobReader il = image.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            var names = new List<string>();
            while (il.RemainingBytes > 0)
            {
                ushort value = il.ReadByte();
                if (value == 0xFE) value = (ushort)(0xFE00 | il.ReadByte());
                System.Reflection.Emit.OpCode opcode = opcodes[value];
                string name = opcode.Name!;
                if (withLocals && name.Split('.') is [("ldloc" or "stloc") and var access, .. var form])
                {
                    int index = form switch
                    {
                        ["s"] => il.ReadByte(),
                        [] => il.ReadUInt16(),
                        _ => int.Parse(form[0])
                    };
                    names.Add($"{access}:{index}");
                    continue;
                }
                names.Add(name);
                int operand = opcode.OperandType switch
                {
                    System.Reflection.Emit.OperandType.InlineNone => 0,
                    System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or
                        System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                    System.Reflection.Emit.OperandType.InlineVar => 2,
                    System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                    System.Reflection.Emit.OperandType.InlineSwitch => 4 * il.ReadInt32(),
                    _ => 4
                };
                il.Offset += operand;
            }
            Console.WriteLine($"{methodName}: {string.Join(' ', names)}");
            found++;
        }
        if (found == 0) throw new InvalidOperationException($"No method named {methodName} in {opcodePath}.");
        return 0;
    }
    if (args is ["vector-calls", string vectorPath])
    {
        // Calls into System.Runtime.Intrinsics' Vector64/128/256 and LlvmNet.Runtime.Vectors, by method name: what the typed
        // vector lowering (ADR-16 Level 1) emitted for an image, for checks that vector IR ran as vector code.
        using FileStream stream = File.OpenRead(vectorPath);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        string TypeName(EntityHandle type)
        {
            if (type.Kind == HandleKind.TypeSpecification)
            {
                BlobReader signature = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)type).Signature);
                if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return "";
                signature.ReadSignatureTypeCode();
                type = signature.ReadTypeHandle();
            }
            if (type.Kind != HandleKind.TypeReference) return "";
            TypeReference reference = metadata.GetTypeReference((TypeReferenceHandle)type);
            return metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name);
        }
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            BlobReader il = image.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            while (il.RemainingBytes > 0)
            {
                ushort value = il.ReadByte();
                if (value == 0xFE) value = (ushort)(0xFE00 | il.ReadByte());
                var opcode = (System.Reflection.Emit.OpCode)typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Select(field => field.GetValue(null)!).First(candidate => (ushort)((System.Reflection.Emit.OpCode)candidate).Value == value);
                if (value is 0x28 or 0x6F)
                {
                    EntityHandle target = MetadataTokens.EntityHandle(il.ReadInt32());
                    if (target.Kind == HandleKind.MethodSpecification) target = metadata.GetMethodSpecification((MethodSpecificationHandle)target).Method;
                    if (target.Kind != HandleKind.MemberReference) continue;
                    MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)target);
                    string owner = TypeName(member.Parent);
                    if (owner.StartsWith("System.Runtime.Intrinsics.Vector", StringComparison.Ordinal) || owner == "LlvmNet.Runtime.Vectors")
                    {
                        string name = (owner == "LlvmNet.Runtime.Vectors" ? "Vectors." : "") + metadata.GetString(member.Name);
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }
                    continue;
                }
                il.Offset += opcode.OperandType switch
                {
                    System.Reflection.Emit.OperandType.InlineNone => 0,
                    System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or
                        System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                    System.Reflection.Emit.OperandType.InlineVar => 2,
                    System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                    System.Reflection.Emit.OperandType.InlineSwitch => 4 * il.ReadInt32(),
                    _ => 4
                };
            }
        }
        Console.WriteLine($"vector-calls: {counts.Values.Sum()}" + (counts.Count == 0 ? "" : " (" + string.Join(", ", counts.Select(entry => $"{entry.Key} {entry.Value}")) + ")"));
        return 0;
    }
    if (args is ["inspect-signatures", string signaturePath])
    {
        using FileStream stream = File.OpenRead(signaturePath);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            Console.WriteLine($"{MetadataTokens.GetToken(handle):x8} {metadata.GetString(metadata.GetTypeDefinition(handle).Name)}");
        for (int row = 1; row <= metadata.GetTableRowCount(TableIndex.StandAloneSig); row++)
        {
            StandaloneSignatureHandle handle = MetadataTokens.StandaloneSignatureHandle(row);
            byte[] signature = metadata.GetBlobBytes(metadata.GetStandaloneSignature(handle).Signature);
            Console.WriteLine($"{MetadataTokens.GetToken(handle):x8} {Convert.ToHexString(signature)}");
        }
        return 0;
    }
#if !LLVMNET_AOT_PROBE
    if (args is ["jit-all", .. string[] roots] && roots.Length > 0)
        return JitValidation.Run(roots);
#endif
    if (args is ["inspect", string assemblyPath])
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        if (image.PEHeaders.CorHeader is null || !image.PEHeaders.CorHeader.Flags.HasFlag(CorFlags.ILOnly))
            throw new InvalidOperationException("Expected an IL-only managed assembly.");
        int methods = 0;
        int imports = 0;
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (method.Attributes.HasFlag(MethodAttributes.PinvokeImpl)) imports++;
            if (method.RelativeVirtualAddress == 0) continue;
            MethodBodyBlock body = image.GetMethodBody(method.RelativeVirtualAddress);
            if (body.GetILContent().IsEmpty) throw new InvalidOperationException("Empty IL method body.");
            methods++;
        }
        Console.WriteLine($"IL-only: {methods} method bodies, {imports} explicit P/Invoke imports.");
        // The C names a library exports (CExport attributes on public static methods), for the visibility tests.
        List<string> exports = [];
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
            foreach (MethodDefinitionHandle methodHandle in metadata.GetTypeDefinition(typeHandle).GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if (!method.Attributes.HasFlag(MethodAttributes.Public)) continue;
                foreach (CustomAttributeHandle attributeHandle in method.GetCustomAttributes())
                {
                    CustomAttribute attribute = metadata.GetCustomAttribute(attributeHandle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                    MemberReference constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (constructor.Parent.Kind != HandleKind.TypeReference ||
                        metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent).Name) != "CExportAttribute") continue;
                    BlobReader reader = metadata.GetBlobReader(attribute.Value);
                    reader.ReadUInt16();
                    exports.Add(reader.ReadSerializedString() ?? "");
                }
            }
        if (exports.Count > 0) Console.WriteLine("exports: " + string.Join(" ", exports.Order(StringComparer.Ordinal)));
        foreach (CustomAttributeHandle attributeHandle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(attributeHandle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            MemberReference constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference ||
                metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent).Name) != "AssemblyMetadataAttribute") continue;
            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1) continue;
            string? key = value.ReadSerializedString();
            string? text = value.ReadSerializedString();
            if (key?.StartsWith("llvmnet.", StringComparison.Ordinal) == true) Console.WriteLine($"{key}: {text}");
        }
        return 0;
    }
    Console.Error.WriteLine("Usage: LlvmNet.Checks compare-numeric expected actual absolute-tolerance | inspect assembly.dll | jit-all path... | inspect-methods assembly.dll name-filter | il-opcodes assembly.dll method [--locals] | inspect-signatures assembly.dll | vector-calls assembly.dll | simd128-probe inputs.bin");
    return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
