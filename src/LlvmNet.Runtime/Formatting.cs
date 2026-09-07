using System.Globalization;
using System.Text;

namespace LlvmNet.Runtime;

public static unsafe class Formatting
{
    [CExport("printf")]
    public static int Printf(nint format, nint arguments)
    {
        byte[] bytes = Format(format, arguments);
        Console.OpenStandardOutput().Write(bytes);
        return bytes.Length;
    }

    [CExport("vprintf")]
    public static int Vprintf(nint format, nint arguments) => Printf(format, arguments);

    [CExport("puts")]
    public static int Puts(nint text)
    {
        Stream output = Console.OpenStandardOutput();
        output.Write(new ReadOnlySpan<byte>((void*)text, checked((int)CString.Strlen(text))));
        output.WriteByte((byte)'\n');
        return 0;
    }

    [CExport("putchar")]
    public static int Putchar(int value)
    {
        Console.OpenStandardOutput().WriteByte((byte)value);
        return (byte)value;
    }

    [CExport("snprintf"), CExport("vsnprintf")]
    public static int Snprintf(nint destination, long size, nint format, nint arguments)
    {
        byte[] bytes = Format(format, arguments);
        if (size > 0)
        {
            int count = (int)Math.Min(bytes.Length, size - 1);
            bytes.AsSpan(0, count).CopyTo(new Span<byte>((void*)destination, count));
            ((byte*)destination)[count] = 0;
        }
        return bytes.Length;
    }

    [CExport("sprintf"), CExport("vsprintf")]
    public static int Sprintf(nint destination, nint format, nint arguments) => Snprintf(destination, long.MaxValue, format, arguments);

    public static byte[] Format(nint format, nint arguments)
    {
        VarArgs values = *(VarArgs*)arguments;
        string pattern = CString.Text(format);
        var result = new StringBuilder();
        for (int index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] != '%')
            {
                result.Append(pattern[index]);
                continue;
            }
            index++;
            if (index == pattern.Length)
                throw new FormatException("Incomplete printf conversion.");
            if (pattern[index] == '%')
            {
                result.Append('%');
                continue;
            }
            bool left = false, plus = false, space = false, alternate = false, zero = false;
            while (index < pattern.Length && "-+ #0'".Contains(pattern[index]))
            {
                switch (pattern[index++])
                {
                    case '-': left = true; break;
                    case '+': plus = true; break;
                    case ' ': space = true; break;
                    case '#': alternate = true; break;
                    case '0': zero = true; break;
                }
            }
            int width = Number();
            if (width < 0)
            {
                left = true;
                width = checked(-width);
            }
            int precision = -1;
            if (index < pattern.Length && pattern[index] == '.')
            {
                index++;
                precision = Number();
            }
            string length = "";
            if (index < pattern.Length && "hljztL".Contains(pattern[index]))
            {
                length += pattern[index++];
                if (index < pattern.Length && length is "h" or "l" && pattern[index] == length[0])
                    length += pattern[index++];
            }
            if (index >= pattern.Length)
                throw new FormatException("Incomplete printf conversion.");
            char conversion = pattern[index];
            string text;
            string prefix = "";
            bool numeric = false;
            if (conversion is 'd' or 'i' or 'u' or 'x' or 'X' or 'o' or 'p')
            {
                numeric = true;
                ulong raw = unchecked((ulong)values.Integer());
                bool signed = conversion is 'd' or 'i';
                if (conversion != 'p')
                {
                    raw = length switch
                    {
                        "hh" => signed ? unchecked((ulong)(sbyte)raw) : (byte)raw,
                        "h" => signed ? unchecked((ulong)(short)raw) : (ushort)raw,
                        "" => signed ? unchecked((ulong)(int)raw) : (uint)raw,
                        _ => raw
                    };
                }
                if (signed && (long)raw < 0)
                {
                    prefix = "-";
                    raw = unchecked(0UL - raw);
                }
                else if (signed)
                    prefix = plus ? "+" : space ? " " : "";
                int radix = conversion is 'x' or 'X' or 'p' ? 16 : conversion == 'o' ? 8 : 10;
                text = Digits(raw, radix, conversion == 'X');
                if (raw == 0 && precision == 0)
                    text = "";
                if (precision > 0)
                    text = text.PadLeft(precision, '0');
                if (conversion == 'p' || alternate && radix == 16 && raw != 0)
                    prefix = conversion == 'X' ? "0X" : "0x";
                if (alternate && radix == 8 && (text.Length == 0 || text[0] != '0'))
                    prefix = "0";
                if (precision >= 0)
                    zero = false;
            }
            else if (conversion is 'f' or 'F' or 'e' or 'E' or 'g' or 'G')
            {
                if (length == "L")
                    throw new NotSupportedException("printf long double is not supported.");
                numeric = true;
                double number = values.Floating();
                bool negative = double.IsNegative(number);
                prefix = negative ? "-" : plus ? "+" : space ? " " : "";
                number = Math.Abs(number);
                int digits = precision < 0 ? 6 : precision;
                if (conversion is 'g' or 'G' && digits == 0)
                    digits = 1;
                text = number.ToString(char.ToUpperInvariant(conversion) + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                int exponent = text.IndexOf('E');
                if (exponent >= 0 && text.Length - exponent == 5 && text[exponent + 2] == '0')
                    text = text.Remove(exponent + 2, 1);
                if (alternate && !text.Contains('.') && !double.IsInfinity(number) && !double.IsNaN(number))
                    text = exponent < 0 ? text + "." : text.Insert(text.IndexOf('E'), ".");
                text = text.Replace("Infinity", "INF", StringComparison.Ordinal).Replace("NaN", "NAN", StringComparison.Ordinal);
                if (char.IsLower(conversion))
                    text = text.ToLowerInvariant();
            }
            else if (conversion == 's')
            {
                nint pointer = (nint)values.Integer();
                if (pointer == 0)
                    text = "(null)";
                else
                {
                    long count = precision < 0 ? CString.Strlen(pointer) : CString.Strnlen(pointer, precision);
                    text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)pointer, checked((int)count)));
                }
            }
            else if (conversion == 'c')
                text = ((char)(byte)values.Integer()).ToString();
            else if (conversion == 'n')
            {
                nint pointer = (nint)values.Integer();
                int count = Encoding.UTF8.GetByteCount(result.ToString());
                switch (length)
                {
                    case "hh": *(byte*)pointer = (byte)count; break;
                    case "h": *(short*)pointer = (short)count; break;
                    case "l": case "ll": case "j": case "z": case "t": *(long*)pointer = count; break;
                    default: *(int*)pointer = count; break;
                }
                continue;
            }
            else
                throw new NotSupportedException($"Unsupported printf conversion %{length}{conversion}");
            int padding = Math.Max(0, width - Encoding.UTF8.GetByteCount(prefix + text));
            if (!left && !(zero && numeric))
                result.Append(' ', padding);
            result.Append(prefix);
            if (!left && zero && numeric)
                result.Append('0', padding);
            result.Append(text);
            if (left)
                result.Append(' ', padding);

            int Number()
            {
                if (index < pattern.Length && pattern[index] == '*')
                {
                    index++;
                    return (int)values.Integer();
                }
                int number = 0;
                while (index < pattern.Length && char.IsAsciiDigit(pattern[index]))
                    number = checked(number * 10 + pattern[index++] - '0');
                return number;
            }
        }
        return Encoding.UTF8.GetBytes(result.ToString());
    }

    private static string Digits(ulong value, int radix, bool uppercase)
    {
        if (radix == 10)
            return value.ToString(CultureInfo.InvariantCulture);
        if (radix == 16)
            return value.ToString(uppercase ? "X" : "x", CultureInfo.InvariantCulture);
        Span<char> buffer = stackalloc char[24];
        int index = buffer.Length;
        do
        {
            buffer[--index] = (char)('0' + value % (ulong)radix);
            value /= (ulong)radix;
        } while (value != 0);
        return new string(buffer[index..]);
    }
}