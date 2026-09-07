using System.Globalization;
using System.Text.RegularExpressions;

namespace LlvmNet.Runtime;

public static unsafe class Parsing
{
    private static readonly Regex realPrefix = new(@"\A[ \t\r\n\v\f]*[+-]?(?:(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?|inf(?:inity)?|nan(?:\([A-Za-z0-9_]*\))?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool Space(char value) => value is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';

    internal static long Integer(ReadOnlySpan<char> text, int radix, bool unsigned, out int consumed, bool binaryPrefix = false)
    {
        consumed = 0;
        if (radix != 0 && radix is < 2 or > 36)
        {
            ProcessRuntime.Error(22);
            return 0;
        }
        int index = 0;
        while (index < text.Length && Space(text[index]))
            index++;
        bool negative = index < text.Length && text[index] == '-';
        if (index < text.Length && text[index] is '-' or '+')
            index++;
        if ((radix is 0 or 16) && index + 2 < text.Length && text[index] == '0' && text[index + 1] is 'x' or 'X' && Digit(text[index + 2]) is >= 0 and < 16)
        {
            index += 2;
            radix = 16;
        }
        if (binaryPrefix && (radix is 0 or 2) && index + 2 < text.Length && text[index] == '0' && text[index + 1] is 'b' or 'B' && text[index + 2] is '0' or '1')
        {
            index += 2;
            radix = 2;
        }
        if (radix == 0)
            radix = index < text.Length && text[index] == '0' ? 8 : 10;
        int first = index;
        ulong limit = unsigned ? ulong.MaxValue : negative ? 1UL << 63 : long.MaxValue;
        ulong value = 0;
        bool overflow = false;
        while (index < text.Length)
        {
            int digit = Digit(text[index]);
            if (digit < 0 || digit >= radix)
                break;
            if (value > (limit - (uint)digit) / (uint)radix)
                overflow = true;
            else if (!overflow)
                value = value * (uint)radix + (uint)digit;
            index++;
        }
        if (index == first)
            return 0;
        consumed = index;
        if (overflow)
        {
            ProcessRuntime.Error(34);
            return unchecked((long)(unsigned ? ulong.MaxValue : negative ? 1UL << 63 : long.MaxValue));
        }
        return unchecked((long)(negative ? 0UL - value : value));
    }

    private static int Digit(char value) => value is >= '0' and <= '9' ? value - '0' : value is >= 'a' and <= 'z' ? value - 'a' + 10 : value is >= 'A' and <= 'Z' ? value - 'A' + 10 : -1;

    [CExport("strtol"), CExport("strtoll")]
    public static long Strtol(nint text, nint end, int radix) => ConvertInteger(text, end, radix, false);
    [CExport("strtoul"), CExport("strtoull")]
    public static long Strtoul(nint text, nint end, int radix) => ConvertInteger(text, end, radix, true);
    [CExport("__isoc23_strtol"), CExport("__isoc23_strtoll")]
    public static long C23Strtol(nint text, nint end, int radix) => ConvertInteger(text, end, radix, false, true);
    [CExport("__isoc23_strtoul"), CExport("__isoc23_strtoull")]
    public static long C23Strtoul(nint text, nint end, int radix) => ConvertInteger(text, end, radix, true, true);
    private static long ConvertInteger(nint text, nint end, int radix, bool unsigned, bool binaryPrefix = false)
    {
        long result = Integer(CString.Text(text), radix, unsigned, out int count, binaryPrefix);
        if (end != 0)
            *(nint*)end = text + count;
        return result;
    }
    [CExport("atoi")]
    public static int Atoi(nint text) => (int)Strtol(text, 0, 10);
    [CExport("atol"), CExport("atoll")]
    public static long Atol(nint text) => Strtol(text, 0, 10);

    internal static double Real(string text, out int consumed)
    {
        Match match = realPrefix.Match(text);
        consumed = match.Length;
        if (!match.Success)
            return 0;
        string token = match.Value.Trim();
        bool negative = token.StartsWith('-');
        string magnitude = token.TrimStart('+', '-');
        if (magnitude.StartsWith("nan", StringComparison.OrdinalIgnoreCase))
            return double.CopySign(double.NaN, negative ? -1 : 1);
        if (magnitude.StartsWith("inf", StringComparison.OrdinalIgnoreCase))
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        double result = double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsInfinity(result) || (Math.Abs(result) < 2.2250738585072014e-308 && magnitude.Split('e', 'E')[0].Any(character => character is >= '1' and <= '9')))
            ProcessRuntime.Error(34);
        return result;
    }

    [CExport("strtod")]
    public static double Strtod(nint text, nint end)
    {
        double result = Real(CString.Text(text), out int count);
        if (end != 0)
            *(nint*)end = text + count;
        return result;
    }
    [CExport("strtof")]
    public static float Strtof(nint text, nint end) => (float)Strtod(text, end);
    [CExport("atof")]
    public static double Atof(nint text) => Strtod(text, 0);
}