using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Runtime;

public static unsafe class FortranIO
{
    private sealed record Unit(nint File, string? Path, bool Owned)
    {
        internal bool AfterEndfile;
    }
    private sealed class Statement(string operation, int unit, string? format = null)
    {
        internal readonly string Operation = operation;
        internal readonly int Unit = unit;
        internal readonly List<FortranEdit>? Format = format is null ? null : FortranFormat.Parse(format);
        internal readonly StringBuilder Output = new();
        internal string? Input;
        internal int Cursor;
        internal int Edit;
        internal bool Plus;
        internal int Scale;
        internal bool Handled;
        internal int Error;
        internal nint Internal;
        internal long InternalLength;
        internal readonly Dictionary<string, string> Options = new(StringComparer.Ordinal);
    }
    private static readonly Dictionary<int, Unit> units = new()
    {
        [5] = new Unit(*(nint*)Stdio.Global("stdin"), null, false),
        [6] = new Unit(*(nint*)Stdio.Global("stdout"), null, false),
        [0] = new Unit(*(nint*)Stdio.Global("stderr"), null, false)
    };
    private static int nextUnit = -10;
    private static Statement Get(nint cookie) => (Statement)GCHandle.FromIntPtr(cookie).Target!;
    private static nint New(Statement statement) => GCHandle.ToIntPtr(GCHandle.Alloc(statement));
    private static string Text(nint text, long length) => Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)text, checked((int)length)));
    private static nint Internal(string operation, nint memory, long length, nint format, long formatLength)
    {
        var statement = new Statement(operation, 0, format == 0 ? null : Text(format, formatLength)) { Internal = memory, InternalLength = length };
        if (operation == "read") statement.Input = Text(memory, length);
        return New(statement);
    }
    [CExport("_FortranAioBeginInternalFormattedOutput")]
    public static nint InternalFormattedOutput(nint memory, long length, nint format, long formatLength, nint formatDescriptor, nint scratch, long scratchSize, nint source, int line) => Internal("write", memory, length, format, formatLength);
    [CExport("_FortranAioBeginInternalFormattedInput")]
    public static nint InternalFormattedInput(nint memory, long length, nint format, long formatLength, nint formatDescriptor, nint scratch, long scratchSize, nint source, int line) => Internal("read", memory, length, format, formatLength);
    [CExport("_FortranAioBeginInternalListInput")]
    public static nint InternalListInput(nint memory, long length, nint scratch, long scratchSize, nint source, int line) => Internal("read", memory, length, 0, 0);
    [CExport("_FortranAioBeginInternalListOutput")]
    public static nint InternalListOutput(nint memory, long length, nint scratch, long scratchSize, nint source, int line) => Internal("write", memory, length, 0, 0);
    [CExport("_FortranAioBeginExternalFormattedOutput")]
    public static nint ExternalFormattedOutput(nint format, long length, nint descriptor, int unit, nint source, int line) => New(new Statement("write", unit, Text(format, length)));
    [CExport("_FortranAioBeginExternalFormattedInput")]
    public static nint ExternalFormattedInput(nint format, long length, nint descriptor, int unit, nint source, int line) => New(new Statement("read", unit, Text(format, length)));
    [CExport("_FortranAioBeginExternalListOutput")]
    public static nint ExternalListOutput(int unit, nint source, int line) => New(new Statement("write", unit));
    [CExport("_FortranAioBeginExternalListInput")]
    public static nint ExternalListInput(int unit, nint source, int line) => New(new Statement("read", unit));
    [CExport("_FortranAioBeginOpenNewUnit")]
    public static nint OpenNewUnit(nint source, int line) => New(new Statement("open", Interlocked.Decrement(ref nextUnit)));
    [CExport("_FortranAioBeginOpenUnit")]
    public static nint OpenUnit(int unit, nint source, int line) => New(new Statement("open", unit));
    [CExport("_FortranAioBeginClose")]
    public static nint Close(int unit, nint source, int line) => New(new Statement("close", unit));
    [CExport("_FortranAioBeginRewind")]
    public static nint Rewind(int unit, nint source, int line) => New(new Statement("rewind", unit));
    [CExport("_FortranAioBeginBackspace")]
    public static nint Backspace(int unit, nint source, int line) => New(new Statement("backspace", unit));
    [CExport("_FortranAioBeginFlush")]
    public static nint Flush(int unit, nint source, int line) => New(new Statement("flush", unit));
    [CExport("_FortranAioEnableHandlers")]
    public static void EnableHandlers(nint cookie, int status, int error, int end, int endRecord, int message) => Get(cookie).Handled = status != 0 || error != 0 || end != 0 || endRecord != 0;
    private static int Option(nint cookie, string key, nint value, long length)
    {
        Get(cookie).Options[key] = Text(value, length).TrimEnd();
        return 1;
    }
    [CExport("_FortranAioSetFile")] public static int SetFile(nint cookie, nint value, long length) => Option(cookie, "file", value, length);
    [CExport("_FortranAioSetStatus")] public static int SetStatus(nint cookie, nint value, long length) => Option(cookie, "status", value, length);
    [CExport("_FortranAioSetAction")] public static int SetAction(nint cookie, nint value, long length) => Option(cookie, "action", value, length);
    [CExport("_FortranAioSetPosition")] public static int SetPosition(nint cookie, nint value, long length) => Option(cookie, "position", value, length);
    [CExport("_FortranAioSetAccess")] public static int SetAccess(nint cookie, nint value, long length) => Option(cookie, "access", value, length);
    [CExport("_FortranAioSetForm")] public static int SetForm(nint cookie, nint value, long length) => Option(cookie, "form", value, length);
    [CExport("_FortranAioGetNewUnit")]
    public static int GetNewUnit(nint cookie, nint destination, int kind)
    {
        if (kind != 4) throw new NotSupportedException("Fortran NEWUNIT requires default INTEGER in this runtime.");
        *(int*)destination = Get(cookie).Unit;
        return 1;
    }

    private static bool Control(Statement state, FortranEdit edit)
    {
        switch (edit.Kind)
        {
            case "literal": if (state.Operation == "write") Put(state, edit.Text); else state.Cursor += edit.Text.Length; return true;
            case "X": if (state.Operation == "write") Put(state, new string(' ', edit.Width)); else state.Cursor += edit.Width; return true;
            case "T": state.Cursor = Math.Max(0, edit.Width - 1); return true;
            case "TL": state.Cursor = Math.Max(0, state.Cursor - edit.Width); return true;
            case "TR": state.Cursor += edit.Width; return true;
            case "P": state.Scale = edit.Width; return true;
            case "SP": state.Plus = true; return true;
            case "S": case "SS": state.Plus = false; return true;
            case "BN": return true;
            case "BZ": throw new NotSupportedException("Fortran BZ input editing is not implemented.");
            case ":": return true;
            case "/":
                if (state.Internal != 0) throw new NotSupportedException("Multiple internal Fortran records are not implemented.");
                for (int index = 0; index < edit.Width; index++)
                {
                    if (state.Operation == "write") Put(state, "\n");
                    else { state.Input = null; state.Cursor = 0; ReadRecord(state); }
                }
                return true;
            default: return false;
        }
    }
    private static FortranEdit? Next(Statement state)
    {
        if (state.Format is null) return null;
        int attempts = 0;
        while (attempts++ <= state.Format.Count * 2)
        {
            if (state.Edit == state.Format.Count)
            {
                state.Edit = 0;
                if (state.Internal != 0) { state.Error = 1001; return null; }
                if (state.Operation == "write") Put(state, "\n");
                else { state.Input = null; state.Cursor = 0; ReadRecord(state); }
            }
            FortranEdit edit = state.Format[state.Edit++];
            if (!Control(state, edit)) return edit;
        }
        throw new FormatException("Fortran format has no data edit descriptors.");
    }
    private static void Put(Statement state, string text)
    {
        if (state.Cursor > state.Output.Length) state.Output.Append(' ', state.Cursor - state.Output.Length);
        int replace = Math.Min(text.Length, state.Output.Length - state.Cursor);
        if (replace > 0) state.Output.Remove(state.Cursor, replace);
        state.Output.Insert(state.Cursor, text);
        state.Cursor += text.Length;
    }
    private static int OutputInteger(nint cookie, long value)
    {
        Statement state = Get(cookie);
        FortranEdit? edit = Next(state);
        Put(state, edit is null ? " " + value.ToString(CultureInfo.InvariantCulture) : FortranFormat.Integer(value, edit, state.Plus));
        return state.Error == 0 ? 1 : 0;
    }
    [CExport("_FortranAioOutputInteger8")] public static int Integer8(nint cookie, int value) => OutputInteger(cookie, (sbyte)value);
    [CExport("_FortranAioOutputInteger16")] public static int Integer16(nint cookie, int value) => OutputInteger(cookie, (short)value);
    [CExport("_FortranAioOutputInteger32")] public static int Integer32(nint cookie, int value) => OutputInteger(cookie, value);
    [CExport("_FortranAioOutputInteger64")] public static int Integer64(nint cookie, long value) => OutputInteger(cookie, value);
    [CExport("_FortranAioOutputReal32")] public static int Real32(nint cookie, float value) => OutputReal(cookie, value);
    [CExport("_FortranAioOutputReal64")] public static int Real64(nint cookie, double value) => OutputReal(cookie, value);
    private static int OutputReal(nint cookie, double value)
    {
        Statement state = Get(cookie);
        FortranEdit? edit = Next(state);
        Put(state, edit is null ? " " + value.ToString("G17", CultureInfo.InvariantCulture) : FortranFormat.Real(value, edit, state.Plus, state.Scale));
        return state.Error == 0 ? 1 : 0;
    }
    [CExport("_FortranAioOutputAscii")]
    public static int Ascii(nint cookie, nint text, long length)
    {
        Statement state = Get(cookie);
        FortranEdit? edit = Next(state);
        string value = Text(text, length);
        if (edit is null) Put(state, " " + value);
        else if (edit.Kind == "A") Put(state, edit.Width == 0 ? value : value.Length > edit.Width ? value[..edit.Width] : value.PadLeft(edit.Width));
        else throw new FormatException("Character value paired with non-character Fortran format.");
        return state.Error == 0 ? 1 : 0;
    }
    [CExport("_FortranAioOutputLogical")]
    public static int Logical(nint cookie, int value)
    {
        Statement state = Get(cookie);
        FortranEdit? edit = Next(state);
        Put(state, edit is null ? value != 0 ? " T" : " F" : FortranFormat.Field(value != 0 ? "T" : "F", edit.Width));
        return 1;
    }
    [CExport("_FortranAioOutputDescriptor")]
    public static int OutputDescriptor(nint cookie, nint descriptor)
    {
        var array = new FortranDescriptor(descriptor);
        for (long index = 0; index < array.Elements; index++)
        {
            nint address = array.Address(index);
            if (array.Type == 40) Ascii(cookie, address, array.ElementBytes);
            else if (array.Type == 27) Real32(cookie, *(float*)address);
            else if (array.Type == 28) Real64(cookie, *(double*)address);
            else if (array.Type is >= 1 and <= 24)
            {
                long value = array.ElementBytes switch { 1 => *(sbyte*)address, 2 => *(short*)address, 4 => *(int*)address, 8 => *(long*)address, _ => throw new NotSupportedException("Fortran integer output kind.") };
                OutputInteger(cookie, value);
            }
            else throw new NotSupportedException($"Fortran output descriptor type {array.Type}");
        }
        return 1;
    }

    private static void ReadRecord(Statement state)
    {
        if (state.Input is not null) return;
        if (!units.TryGetValue(state.Unit, out Unit? unit)) { state.Error = 1002; state.Input = ""; return; }
        List<byte> bytes = [];
        int value;
        while ((value = Stdio.Getc(unit.File)) >= 0 && value != '\n') bytes.Add((byte)value);
        unit.AfterEndfile = value < 0 && bytes.Count == 0;
        if (unit.AfterEndfile) state.Error = -1;
        if (bytes.Count != 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
        state.Input = Encoding.UTF8.GetString(bytes.ToArray());
    }
    private static string? InputToken(Statement state)
    {
        ReadRecord(state);
        FortranEdit? edit = Next(state);
        if (edit is not null)
        {
            int length = edit.Width == 0 ? state.Input!.Length - state.Cursor : edit.Width;
            string token = state.Cursor >= state.Input!.Length ? "" : state.Input.Substring(state.Cursor, Math.Min(length, state.Input.Length - state.Cursor));
            state.Cursor += length;
            return token;
        }
        while (true)
        {
            while (state.Cursor < state.Input!.Length && (Parsing.Space(state.Input[state.Cursor]) || state.Input[state.Cursor] == ',')) state.Cursor++;
            if (state.Cursor < state.Input.Length) break;
            if (state.Internal != 0 || state.Error != 0) { state.Error = -1; return null; }
            state.Input = null;
            state.Cursor = 0;
            ReadRecord(state);
        }
        int start = state.Cursor;
        if (state.Input![state.Cursor] is '\'' or '"')
        {
            char quote = state.Input[state.Cursor++];
            start = state.Cursor;
            while (state.Cursor < state.Input.Length && state.Input[state.Cursor] != quote) state.Cursor++;
            string result = state.Input[start..state.Cursor];
            if (state.Cursor < state.Input.Length) state.Cursor++;
            return result;
        }
        while (state.Cursor < state.Input.Length && !Parsing.Space(state.Input[state.Cursor]) && state.Input[state.Cursor] is not (',' or '/')) state.Cursor++;
        if (state.Cursor == start) { state.Error = -1; return null; }
        return state.Input[start..state.Cursor];
    }
    [CExport("_FortranAioInputInteger")]
    public static int InputInteger(nint cookie, nint destination, int kind)
    {
        Statement state = Get(cookie);
        string? token = InputToken(state);
        if (token is null) return 0;
        long value = Parsing.Integer(token, 10, false, out int consumed);
        if (consumed == 0) { state.Error = 1003; return 0; }
        switch (kind)
        {
            case 1: *(sbyte*)destination = (sbyte)value; break;
            case 2: *(short*)destination = (short)value; break;
            case 4: *(int*)destination = (int)value; break;
            case 8: *(long*)destination = value; break;
            default: throw new NotSupportedException($"Fortran INTEGER(kind={kind}) input");
        }
        return 1;
    }
    [CExport("_FortranAioInputReal64")]
    public static int InputReal64(nint cookie, nint destination) => InputReal(cookie, destination, false);
    [CExport("_FortranAioInputReal32")]
    public static int InputReal32(nint cookie, nint destination) => InputReal(cookie, destination, true);
    private static int InputReal(nint cookie, nint destination, bool single)
    {
        Statement state = Get(cookie);
        string? token = InputToken(state);
        if (token is null) return 0;
        double value = Parsing.Real(token.Replace('D', 'E').Replace('d', 'e'), out int consumed);
        if (consumed == 0) { state.Error = 1003; return 0; }
        if (single) *(float*)destination = (float)value;
        else *(double*)destination = value;
        return 1;
    }
    [CExport("_FortranAioInputAscii")]
    public static int InputAscii(nint cookie, nint destination, long length)
    {
        Statement state = Get(cookie);
        string? text = InputToken(state);
        if (text is null) return 0;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        NativeMemory.Fill((void*)destination, (nuint)length, (byte)' ');
        bytes.AsSpan(0, (int)Math.Min(bytes.Length, length)).CopyTo(new Span<byte>((void*)destination, checked((int)length)));
        return 1;
    }
    [CExport("_FortranAioInputDescriptor")]
    public static int InputDescriptor(nint cookie, nint descriptor)
    {
        var array = new FortranDescriptor(descriptor);
        for (long index = 0; index < array.Elements; index++)
        {
            nint address = array.Address(index);
            int result = array.Type switch
            {
                40 => InputAscii(cookie, address, array.ElementBytes),
                27 => InputReal32(cookie, address),
                28 => InputReal64(cookie, address),
                >= 1 and <= 24 => InputInteger(cookie, address, checked((int)array.ElementBytes)),
                _ => throw new NotSupportedException($"Fortran input descriptor type {array.Type}")
            };
            if (result == 0) return 0;
        }
        return 1;
    }

    [CExport("_FortranAioEndIoStatement")]
    public static int End(nint cookie)
    {
        Statement state = Get(cookie);
        try
        {
            switch (state.Operation)
            {
                case "write":
                    if (state.Format is not null)
                        while (state.Edit < state.Format.Count && state.Format[state.Edit].Kind != ":" && Control(state, state.Format[state.Edit])) state.Edit++;
                    byte[] bytes = Encoding.UTF8.GetBytes(state.Output.ToString());
                    if (state.Internal != 0)
                    {
                        if (bytes.Length > state.InternalLength) state.Error = 1001;
                        else
                        {
                            NativeMemory.Fill((void*)state.Internal, (nuint)state.InternalLength, (byte)' ');
                            bytes.CopyTo(new Span<byte>((void*)state.Internal, bytes.Length));
                        }
                    }
                    else if (units.TryGetValue(state.Unit, out Unit? output))
                    {
                        fixed (byte* pointer = bytes)
                            if (Stdio.Write(Stdio.Fileno(output.File), (nint)pointer, bytes.Length) < 0) state.Error = 1002;
                        Stdio.Putc('\n', output.File);
                    }
                    else state.Error = 1002;
                    break;
                case "open": Open(state); break;
                case "close":
                    if (units.Remove(state.Unit, out Unit? closing))
                    {
                        if (closing.Owned) Stdio.Fclose(closing.File);
                        if (state.Options.GetValueOrDefault("status", "keep").Equals("delete", StringComparison.OrdinalIgnoreCase) && closing.Path is not null) File.Delete(closing.Path);
                    }
                    break;
                case "rewind":
                    if (units.TryGetValue(state.Unit, out Unit? rewind))
                    {
                        state.Error = Stdio.Fseek(rewind.File, 0, 0);
                        if (state.Error == 0) { rewind.AfterEndfile = false; Stdio.Clearerr(rewind.File); }
                    }
                    else state.Error = 1002;
                    break;
                case "backspace": BackspaceRecord(state); break;
                case "flush":
                    if (units.TryGetValue(state.Unit, out Unit? flush)) state.Error = Stdio.Flush(flush.File);
                    else state.Error = 1002;
                    break;
            }
            if (state.Error != 0 && !state.Handled) throw new IOException($"Fortran {state.Operation} failed with IOSTAT={state.Error} on unit {state.Unit}.");
            return state.Error;
        }
        finally { GCHandle.FromIntPtr(cookie).Free(); }
    }

    private static void BackspaceRecord(Statement state)
    {
        if (!units.TryGetValue(state.Unit, out Unit? unit)) { state.Error = 1002; return; }
        if (unit.AfterEndfile)
        {
            unit.AfterEndfile = false;
            Stdio.Clearerr(unit.File);
            return;
        }
        long position = Stdio.Ftell(unit.File);
        if (position < 0) { state.Error = 1002; return; }
        long cursor = position - 1;
        while (cursor >= 0)
        {
            if (Stdio.Fseek(unit.File, cursor, 0) != 0) { state.Error = 1002; return; }
            int value = Stdio.Getc(unit.File);
            if (value < 0) { state.Error = 1002; return; }
            if (value == '\n' && cursor != position - 1) break;
            cursor--;
        }
        state.Error = Stdio.Fseek(unit.File, cursor + 1, 0);
        if (state.Error == 0) Stdio.Clearerr(unit.File);
    }

    private static void Open(Statement state)
    {
        string path = state.Options.GetValueOrDefault("file", $"fort.{state.Unit}");
        string disposition = state.Options.GetValueOrDefault("status", "unknown").ToLowerInvariant();
        string action = state.Options.GetValueOrDefault("action", "readwrite").ToLowerInvariant();
        if (state.Options.GetValueOrDefault("form", "formatted").ToLowerInvariant() != "formatted" || state.Options.GetValueOrDefault("access", "sequential").ToLowerInvariant() != "sequential")
            throw new NotSupportedException("Only sequential formatted Fortran units are implemented.");
        if (disposition == "old" && !File.Exists(path) || disposition == "new" && File.Exists(path)) { state.Error = 1002; return; }
        string mode = action == "read" ? "r" : disposition == "replace" || !File.Exists(path) ? "w+" : "r+";
        nint pathPointer = Marshal.StringToCoTaskMemUTF8(path);
        nint modePointer = Marshal.StringToCoTaskMemUTF8(mode);
        try
        {
            nint file = Stdio.Fopen(pathPointer, modePointer);
            if (file == 0) { state.Error = *(int*)ProcessRuntime.ErrnoLocation(); return; }
            if (state.Options.GetValueOrDefault("position", "asis").Equals("append", StringComparison.OrdinalIgnoreCase)) Stdio.Fseek(file, 0, 2);
            if (units.Remove(state.Unit, out Unit? previous) && previous.Owned) Stdio.Fclose(previous.File);
            units[state.Unit] = new Unit(file, path, true);
        }
        finally { Marshal.FreeCoTaskMem(pathPointer); Marshal.FreeCoTaskMem(modePointer); }
    }
}