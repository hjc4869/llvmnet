using System.Text;

namespace LlvmNet.Runtime;

public static unsafe class Scanning
{
    private sealed class Input(Func<int> read)
    {
        private readonly List<byte> buffered = [];
        private bool ended;
        internal int Position;
        internal int Peek(int offset = 0)
        {
            int index = Position + offset;
            while (!ended && buffered.Count <= index)
            {
                int value = read();
                if (value < 0) ended = true;
                else buffered.Add((byte)value);
            }
            return index < buffered.Count ? buffered[index] : -1;
        }
        internal string Prefix(int maximum, Func<int, bool> accept)
        {
            var text = new StringBuilder();
            while (text.Length < maximum && Peek(text.Length) is int value && value >= 0 && accept(value))
                text.Append((char)value);
            return text.ToString();
        }
        internal void Restore(nint file)
        {
            for (int index = buffered.Count - 1; index >= Position; index--)
                Stdio.Ungetc(buffered[index], file);
        }
    }

    [CExport("sscanf"), CExport("__isoc99_sscanf"), CExport("vsscanf"), CExport("__isoc99_vsscanf")]
    public static int Sscanf(nint input, nint format, nint arguments) => StringScan(input, format, arguments, false);
    [CExport("__isoc23_sscanf"), CExport("__isoc23_vsscanf")]
    public static int C23Sscanf(nint input, nint format, nint arguments) => StringScan(input, format, arguments, true);
    private static int StringScan(nint input, nint format, nint arguments, bool binary)
    {
        long cursor = 0;
        var source = new Input(() => { int value = ((byte*)input)[cursor++]; return value == 0 ? -1 : value; });
        return Scan(source, CString.Text(format), *(VarArgs*)arguments, binary);
    }
    [CExport("fscanf"), CExport("__isoc99_fscanf"), CExport("vfscanf"), CExport("__isoc99_vfscanf")]
    public static int Fscanf(nint file, nint format, nint arguments) => FileScan(file, format, arguments, false);
    [CExport("__isoc23_fscanf"), CExport("__isoc23_vfscanf")]
    public static int C23Fscanf(nint file, nint format, nint arguments) => FileScan(file, format, arguments, true);
    [CExport("scanf"), CExport("__isoc99_scanf"), CExport("vscanf")]
    public static int Scanf(nint format, nint arguments) => Fscanf(*(nint*)Stdio.Global("stdin"), format, arguments);
    private static int FileScan(nint file, nint format, nint arguments, bool binary)
    {
        var source = new Input(() => Stdio.Getc(file));
        try { return Scan(source, CString.Text(format), *(VarArgs*)arguments, binary); }
        finally { source.Restore(file); }
    }

    private static int Scan(Input input, string pattern, VarArgs values, bool binary)
    {
        int assigned = 0;
        int conversions = 0;
        int Failure() => conversions == 0 && input.Peek() < 0 ? -1 : assigned;
        for (int index = 0; index < pattern.Length; index++)
        {
            if (Parsing.Space(pattern[index]))
            {
                while (input.Peek() >= 0 && Parsing.Space((char)input.Peek()))
                    input.Position++;
                continue;
            }
            if (pattern[index] != '%' || index + 1 < pattern.Length && pattern[index + 1] == '%')
            {
                char expected = pattern[index] == '%' ? pattern[++index] : pattern[index];
                if (input.Peek() != expected)
                    return Failure();
                input.Position++;
                continue;
            }
            if (++index == pattern.Length) throw new FormatException("Incomplete scanf conversion.");
            bool suppress = pattern[index] == '*';
            if (suppress)
                index++;
            int width = 0;
            while (index < pattern.Length && char.IsAsciiDigit(pattern[index]))
                width = checked(width * 10 + pattern[index++] - '0');
            string length = "";
            if (index < pattern.Length && "hljztL".Contains(pattern[index]))
            {
                length += pattern[index++];
                if (index < pattern.Length && length is "h" or "l" && pattern[index] == length[0])
                    length += pattern[index++];
            }
            if (index >= pattern.Length)
                throw new FormatException("Incomplete scanf conversion.");
            char conversion = pattern[index];
            nint destination = suppress ? 0 : (nint)values.Integer();
            if (conversion == 'n')
            {
                if (!suppress)
                    StoreInteger(destination, input.Position, length);
                conversions++;
                continue;
            }
            if (conversion is not ('c' or '['))
                while (input.Peek() >= 0 && Parsing.Space((char)input.Peek()))
                    input.Position++;
            if (input.Peek() < 0)
                return Failure();
            int maximum = width == 0 ? int.MaxValue : width;
            int consumed;
            if (conversion is 'd' or 'i' or 'u' or 'x' or 'X' or 'o' or 'p')
            {
                int radix = conversion switch { 'i' => 0, 'o' => 8, 'x' or 'X' or 'p' => 16, _ => 10 };
                string prefix = input.Prefix(maximum, character => char.IsAsciiLetterOrDigit((char)character) || character is '+' or '-');
                long number = Parsing.Integer(prefix, radix, conversion is not ('d' or 'i'), out consumed, binary);
                if (consumed == 0)
                    return assigned;
                if (!suppress)
                    StoreInteger(destination, number, conversion == 'p' ? "l" : length);
            }
            else if ("fFeEgGaA".Contains(conversion))
            {
                string prefix = input.Prefix(maximum, character => char.IsAsciiLetterOrDigit((char)character) || character is '+' or '-' or '.' or '(' or ')' or '_');
                double number = Parsing.Real(prefix, out consumed);
                if (consumed == 0)
                    return assigned;
                if (length == "L")
                    throw new NotSupportedException("scanf long double is not implemented.");
                if (!suppress)
                {
                    if (length == "l")
                        *(double*)destination = number;
                    else
                        *(float*)destination = (float)number;
                }
            }
            else if (conversion is 's' or 'c' or '[')
            {
                if (length != "")
                    throw new NotSupportedException("Wide character scanf conversions are not implemented.");
                consumed = 0;
                HashSet<int>? accepted = null;
                bool inverted = false;
                if (conversion == '[')
                {
                    accepted = [];
                    index++;
                    if (index < pattern.Length && pattern[index] == '^') { inverted = true; index++; }
                    if (index < pattern.Length && pattern[index] == ']') accepted.Add(pattern[index++]);
                    while (index < pattern.Length && pattern[index] != ']')
                    {
                        int first = pattern[index++];
                        if (index + 1 < pattern.Length && pattern[index] == '-' && pattern[index + 1] != ']')
                        {
                            int last = pattern[index + 1];
                            index += 2;
                            for (int item = first; item <= last; item++) accepted.Add(item);
                        }
                        else accepted.Add(first);
                    }
                    if (index == pattern.Length) throw new FormatException("Unterminated scanf scanset.");
                }
                if (conversion == 'c' && width == 0) maximum = 1;
                while (consumed < maximum && input.Peek(consumed) is int character && character >= 0 &&
                       (conversion == 'c' || (conversion == 's' ? !Parsing.Space((char)character) : accepted!.Contains(character) != inverted)))
                {
                    if (!suppress) ((byte*)destination)[consumed] = (byte)character;
                    consumed++;
                }
                if (consumed == 0)
                    return assigned;
                if (!suppress && conversion != 'c') ((byte*)destination)[consumed] = 0;
            }
            else
                throw new NotSupportedException($"Unsupported scanf conversion %{length}{conversion}");
            input.Position += consumed;
            conversions++;
            if (!suppress)
                assigned++;
        }
        return assigned;
    }

    private static void StoreInteger(nint destination, long value, string length)
    {
        switch (length)
        {
            case "hh": *(byte*)destination = (byte)value; break;
            case "h": *(short*)destination = (short)value; break;
            case "l": case "ll": case "z": case "t": case "j": *(long*)destination = value; break;
            default: *(int*)destination = (int)value; break;
        }
    }
}