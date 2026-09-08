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
        return 0;
    }
    Console.Error.WriteLine("Usage: LlvmNet.Checks compare-numeric expected actual absolute-tolerance | inspect assembly.dll | inspect-methods assembly.dll name-filter | inspect-signatures assembly.dll | simd128-probe inputs.bin");
    return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}