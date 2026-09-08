using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Runtime;

public static unsafe partial class FortranIO
{
    private readonly record struct NamelistToken(string Text, bool Quoted = false);

    [CExport("_FortranAioOutputNamelist")]
    public static int OutputNamelist(nint cookie, nint group)
    {
        Statement state = Get(cookie);
        string delimiter = state.Options.GetValueOrDefault("delim", units.GetValueOrDefault(state.Unit)?.Delimiter ?? "none");
        Put(state, " &" + CString.Text(*(nint*)group).ToUpperInvariant());
        long count = *(long*)(group + 8);
        nint items = *(nint*)(group + 16);
        for (long index = 0; index < count; index++)
        {
            nint item = items + checked((nint)(index * 16));
            var array = new FortranDescriptor(*(nint*)(item + 8));
            if (array.Elements == 0) continue;
            Put(state, (state.Internal == 0 ? "\n " : " ") + CString.Text(*(nint*)item).ToUpperInvariant() + "=");
            for (long element = 0; element < array.Elements; element++)
            {
                string value = NamelistOutputValue(array.Type, array.ElementBytes, array.Address(element), delimiter);
                if (state.Internal == 0 && state.Cursor - state.RecordStart + value.Length + 1 > 79)
                    Put(state, "\n ");
                Put(state, value + (array.Type == 40 && delimiter == "none" ? "" : ","));
            }
        }
        Put(state, state.Internal == 0 ? "\n /" : " /");
        return state.Error == 0 ? 1 : 0;
    }

    private static string NamelistOutputValue(int type, long bytes, nint address, string delimiter)
    {
        if (type == 40)
        {
            string value = Text(address, bytes);
            if (delimiter == "none") return value;
            string quote = delimiter == "quote" ? "\"" : "'";
            return quote + value.Replace(quote, quote + quote, StringComparison.Ordinal) + quote;
        }
        if (type is >= 1 and <= 11 or >= 17 and <= 24)
        {
            long value = bytes switch
            {
                1 => *(sbyte*)address,
                2 => *(short*)address,
                4 => *(int*)address,
                8 => *(long*)address,
                _ => throw new NotSupportedException("Namelist integer kind is not implemented.")
            };
            return value.ToString(CultureInfo.InvariantCulture);
        }
        if (type == 27) return (*(float*)address).ToString("G9", CultureInfo.InvariantCulture);
        if (type == 28) return (*(double*)address).ToString("G17", CultureInfo.InvariantCulture);
        if (type is 34 or 35)
            return "(" + NamelistOutputValue(type - 7, bytes / 2, address, delimiter) + "," +
                NamelistOutputValue(type - 7, bytes / 2, address + (nint)(bytes / 2), delimiter) + ")";
        if (type is >= 12 and <= 16 or 39)
        {
            bool value = new ReadOnlySpan<byte>((void*)address, checked((int)bytes)).ContainsAnyExcept((byte)0);
            return value ? "T" : "F";
        }
        throw new NotSupportedException($"Namelist output descriptor type {type} is not implemented.");
    }

    private sealed class NamelistInput(Statement state)
    {
        private readonly List<NamelistToken> lookahead = [];

        internal NamelistToken Peek(int offset = 0)
        {
            while (lookahead.Count <= offset) lookahead.Add(Lex());
            return lookahead[offset];
        }

        internal NamelistToken Take()
        {
            NamelistToken token = Peek();
            lookahead.RemoveAt(0);
            return token;
        }

        internal bool Accept(string text)
        {
            if (Peek().Quoted || !Peek().Text.Equals(text, StringComparison.OrdinalIgnoreCase)) return false;
            Take();
            return true;
        }

        internal void Require(string text)
        {
            if (!Accept(text)) throw new FormatException($"Expected '{text}' in namelist input.");
        }

        private bool Record()
        {
            ReadRecord(state);
            while (state.Cursor >= state.Input!.Length)
            {
                if (state.Internal != 0 || state.Error != 0) return false;
                state.Input = null;
                state.Cursor = 0;
                ReadRecord(state);
            }
            return true;
        }

        private NamelistToken Lex()
        {
            while (Record())
            {
                char value = state.Input![state.Cursor];
                if (char.IsWhiteSpace(value)) { state.Cursor++; continue; }
                if (value == '!') { state.Cursor = state.Input.Length; continue; }
                if (value is '\'' or '"')
                {
                    state.Cursor++;
                    var quoted = new StringBuilder();
                    while (Record())
                    {
                        char character = state.Input![state.Cursor++];
                        if (character != value) { quoted.Append(character); continue; }
                        if (state.Cursor < state.Input.Length && state.Input[state.Cursor] == value)
                        {
                            state.Cursor++;
                            quoted.Append(value);
                            continue;
                        }
                        return new NamelistToken(quoted.ToString(), true);
                    }
                    throw new FormatException("Unterminated namelist character value.");
                }
                if ("&$/=():%,*".Contains(value))
                {
                    state.Cursor++;
                    return new NamelistToken(value.ToString());
                }
                int start = state.Cursor++;
                while (state.Cursor < state.Input.Length && !char.IsWhiteSpace(state.Input[state.Cursor]) &&
                    !"&$/=():%,*!\"'".Contains(state.Input[state.Cursor])) state.Cursor++;
                return new NamelistToken(state.Input[start..state.Cursor]);
            }
            return new NamelistToken("");
        }
    }

    [CExport("_FortranAioInputNamelist")]
    public static int InputNamelist(nint cookie, nint group)
    {
        Statement state = Get(cookie);
        try
        {
            string groupName = CString.Text(*(nint*)group);
            long count = *(long*)(group + 8);
            nint items = *(nint*)(group + 16);
            var variables = new Dictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
            for (long index = 0; index < count; index++)
                variables.Add(CString.Text(*(nint*)(items + (nint)(index * 16))), *(nint*)(items + (nint)(index * 16 + 8)));
            var input = new NamelistInput(state);
            while (true)
            {
                if (input.Peek().Text.Length == 0) { state.Error = -1; return 0; }
                if (!(input.Accept("&") || input.Accept("$"))) { input.Take(); continue; }
                if (input.Take().Text.Equals(groupName, StringComparison.OrdinalIgnoreCase)) break;
            }
            while (true)
            {
                if (input.Accept("/")) return 1;
                if (input.Accept("&") || input.Accept("$")) { input.Require("end"); return 1; }
                if (input.Accept(",")) continue;
                NamelistToken name = input.Take();
                if (name.Quoted || !variables.TryGetValue(name.Text, out nint descriptor))
                    throw new FormatException($"Unknown namelist variable '{name.Text}'.");
                var array = new FortranDescriptor(descriptor);
                List<nint> addresses = NamelistAddresses(input, array);
                if (input.Accept("%")) throw new NotSupportedException("Namelist derived-type components are not implemented.");
                input.Require("=");
                int position = 0;
                bool comma = false;
                while (true)
                {
                    NamelistToken token = input.Peek();
                    if (token.Text.Length == 0 && !token.Quoted) throw new FormatException("Unterminated namelist group.");
                    if (!token.Quoted && token.Text is "/" or "&" or "$") break;
                    if (!token.Quoted && (char.IsLetter(token.Text[0]) || token.Text[0] == '_') &&
                        input.Peek(1).Text is "=" or "(" or "%") break;
                    if (input.Accept(","))
                    {
                        if (!comma) position++;
                        comma = false;
                        continue;
                    }
                    int repeats = 1;
                    if (!token.Quoted && int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int repeated) && input.Peek(1).Text == "*")
                    {
                        if (repeated <= 0) throw new FormatException("Invalid namelist repeat count.");
                        repeats = repeated;
                        input.Take();
                        input.Take();
                        if (input.Peek().Text is "," or "/" or "&" or "$")
                        {
                            position = checked(position + repeats);
                            comma = true;
                            continue;
                        }
                    }
                    token = input.Take();
                    string? imaginary = null;
                    if (!token.Quoted && token.Text == "(")
                    {
                        token = input.Take();
                        input.Require(",");
                        imaginary = input.Take().Text;
                        input.Require(")");
                    }
                    for (int repeat = 0; repeat < repeats; repeat++)
                    {
                        if (position >= addresses.Count) throw new FormatException("Too many namelist values.");
                        NamelistStore(array.Type, array.ElementBytes, addresses[position++], token, imaginary);
                    }
                    comma = true;
                }
            }
        }
        catch (Exception error) when (error is FormatException or OverflowException or IndexOutOfRangeException)
        {
            state.Error = 1003;
            state.Options["iomsg"] = error.Message;
            return 0;
        }
    }

    private static List<nint> NamelistAddresses(NamelistInput input, FortranDescriptor array)
    {
        var result = new List<nint>();
        if (!input.Accept("("))
        {
            for (long index = 0; index < array.Elements; index++) result.Add(array.Address(index));
            return result;
        }
        if (array.Rank == 0) throw new NotSupportedException("Namelist substrings are not implemented.");
        var dimensions = new List<(long First, long Last, long Step)>();
        bool section = false;
        for (int dimension = 0; dimension < array.Rank; dimension++)
        {
            long lower = *(long*)(array.Handle + 24 + dimension * 24);
            long upper = checked(lower + array.Extent(dimension) - 1);
            long first = input.Peek().Text == ":" ? lower : long.Parse(input.Take().Text, CultureInfo.InvariantCulture);
            long last = first;
            long step = 1;
            if (input.Accept(":"))
            {
                section = true;
                last = input.Peek().Text is "," or ")" or ":" ? upper : long.Parse(input.Take().Text, CultureInfo.InvariantCulture);
                if (input.Accept(":")) step = long.Parse(input.Take().Text, CultureInfo.InvariantCulture);
            }
            if (step == 0 || first < lower || first > upper || last < lower || last > upper)
                throw new FormatException("Namelist subscript is outside its array bounds.");
            dimensions.Add((first - lower, last - lower, step));
            input.Require(dimension + 1 == array.Rank ? ")" : ",");
        }
        if (!section)
        {
            long index = 0;
            long stride = 1;
            for (int dimension = 0; dimension < array.Rank; dimension++)
            {
                index = checked(index + dimensions[dimension].First * stride);
                stride = checked(stride * array.Extent(dimension));
            }
            for (; index < array.Elements; index++) result.Add(array.Address(index));
        }
        else
            Append(array.Rank - 1, array.Base);
        return result;

        void Append(int dimension, nint address)
        {
            if (dimension < 0) { result.Add(address); return; }
            var range = dimensions[dimension];
            for (long index = range.First; range.Step > 0 ? index <= range.Last : index >= range.Last; index += range.Step)
                Append(dimension - 1, address + checked((nint)(index * array.Stride(dimension))));
        }
    }

    private static void NamelistStore(int type, long bytes, nint address, NamelistToken token, string? imaginary)
    {
        if (type == 40)
        {
            byte[] text = Encoding.UTF8.GetBytes(token.Text);
            NativeMemory.Fill((void*)address, checked((nuint)bytes), (byte)' ');
            text.AsSpan(0, checked((int)Math.Min(text.Length, bytes))).CopyTo(new Span<byte>((void*)address, checked((int)bytes)));
        }
        else if (type is >= 1 and <= 11 or >= 17 and <= 24)
        {
            long value = long.Parse(token.Text, CultureInfo.InvariantCulture);
            switch (bytes)
            {
                case 1: *(sbyte*)address = checked((sbyte)value); break;
                case 2: *(short*)address = checked((short)value); break;
                case 4: *(int*)address = checked((int)value); break;
                case 8: *(long*)address = value; break;
                default: throw new NotSupportedException("Namelist integer kind is not implemented.");
            }
        }
        else if (type is 27 or 28)
        {
            double value = double.Parse(token.Text.Replace('D', 'E').Replace('d', 'e'), CultureInfo.InvariantCulture);
            if (type == 27) *(float*)address = (float)value;
            else *(double*)address = value;
        }
        else if (type is 34 or 35)
        {
            if (imaginary is null) throw new FormatException("Complex namelist value requires two components.");
            NamelistStore(type - 7, bytes / 2, address, token, null);
            NamelistStore(type - 7, bytes / 2, address + (nint)(bytes / 2), new NamelistToken(imaginary), null);
        }
        else if (type is >= 12 and <= 16 or 39)
        {
            string logical = token.Text.TrimStart('.');
            if (logical.Length == 0 || char.ToUpperInvariant(logical[0]) is not ('T' or 'F'))
                throw new FormatException("Invalid logical namelist value.");
            NativeMemory.Clear((void*)address, checked((nuint)bytes));
            if (char.ToUpperInvariant(logical[0]) == 'T') *(byte*)address = 1;
        }
        else throw new NotSupportedException($"Namelist descriptor type {type} is not implemented.");
    }
}