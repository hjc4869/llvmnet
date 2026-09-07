using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

try
{
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
    Console.Error.WriteLine("Usage: LlvmNet.Checks compare-numeric expected actual absolute-tolerance | inspect assembly.dll");
    return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}