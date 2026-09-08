using System.Buffers.Binary;

namespace LlvmNet.Runtime;

public static unsafe partial class FortranIO
{
    [CExport("_FortranAioBeginUnformattedInput")]
    public static nint BeginUnformattedInput(int unit, nint source, int line) => New(new Statement("read-unformatted", unit));
    [CExport("_FortranAioBeginUnformattedOutput")]
    public static nint BeginUnformattedOutput(int unit, nint source, int line) => New(new Statement("write-unformatted", unit));

    private static int TransferUnformatted(Statement state, FortranDescriptor array, bool output)
    {
        if (state.Error != 0) return 0;
        if (!units.TryGetValue(state.Unit, out Unit? unit) || !unit.Unformatted) { state.Error = 1002; return 0; }
        if (unit.Direct && state.RecordNumber is null) { state.Error = 1003; return 0; }
        if (!output && !unit.Stream && state.BinaryInput is null &&
            !(unit.Direct ? ReadDirectRecord(state, unit) : ReadUnformattedRecord(state, unit))) return 0;
        if (output) state.BinaryOutput ??= new MemoryStream();
        int size = checked((int)array.ElementBytes);
        int component = array.Type is >= 32 and <= 38 ? size / 2 : array.Type == 40 ? 1 : size;
        for (long index = 0; index < array.Elements; index++)
        {
            nint address = array.Address(index);
            if (output)
            {
                var bytes = new ReadOnlySpan<byte>((void*)address, size);
                if (unit.Swap && component > 1)
                {
                    byte[] converted = bytes.ToArray();
                    SwapUnformatted(converted, component);
                    state.BinaryOutput!.Write(converted);
                }
                else state.BinaryOutput!.Write(bytes);
            }
            else
            {
                var bytes = new Span<byte>((void*)address, size);
                if (unit.Stream)
                {
                    if (Stdio.Fread(address, 1, size, unit.File) != size) { state.Error = -1; unit.AfterEndfile = true; return 0; }
                }
                else
                {
                    if (size > state.BinaryInput!.Length - state.BinaryCursor) { state.Error = 1003; return 0; }
                    state.BinaryInput.AsSpan(state.BinaryCursor, size).CopyTo(bytes);
                    state.BinaryCursor += size;
                }
                if (unit.Swap && component > 1) SwapUnformatted(bytes, component);
            }
        }
        return 1;
    }

    private static void SwapUnformatted(Span<byte> bytes, int component)
    {
        for (int offset = 0; offset < bytes.Length; offset += component)
            bytes.Slice(offset, component).Reverse();
    }

    private static bool ReadUnformattedRecord(Statement state, Unit unit)
    {
        int marker = 0;
        long read = Stdio.Fread((nint)(&marker), 1, 4, unit.File);
        if (read == 0) { state.Error = -1; unit.AfterEndfile = true; return false; }
        if (read != 4) { state.Error = 1003; return false; }
        int length = unit.Swap ? BinaryPrimitives.ReverseEndianness(marker) : marker;
        if (length < 0) throw new NotSupportedException("Segmented unformatted Fortran records are not implemented.");
        state.BinaryInput = new byte[length];
        fixed (byte* data = state.BinaryInput)
            if (Stdio.Fread((nint)data, 1, length, unit.File) != length) { state.Error = 1003; return false; }
        int trailing = 0;
        if (Stdio.Fread((nint)(&trailing), 1, 4, unit.File) != 4 || trailing != marker) { state.Error = 1003; return false; }
        unit.AfterEndfile = false;
        return true;
    }

    private static bool ReadDirectRecord(Statement state, Unit unit)
    {
        int length = checked((int)unit.RecordLength!.Value);
        state.BinaryInput = new byte[length];
        fixed (byte* bytes = state.BinaryInput)
            if (Stdio.Fread((nint)bytes, 1, length, unit.File) != length) { state.Error = 1003; return false; }
        return true;
    }

    private static void EndUnformatted(Statement state)
    {
        if (state.Error != 0) return;
        if (!units.TryGetValue(state.Unit, out Unit? unit) || !unit.Unformatted) { state.Error = 1002; return; }
        byte[] data = state.BinaryOutput?.ToArray() ?? [];
        if (!unit.Stream && unit.RecordLength is long maximum && data.Length > maximum) { state.Error = 1001; return; }
        if (unit.Direct)
        {
            if (state.RecordNumber is null) { state.Error = 1003; return; }
            Array.Resize(ref data, checked((int)unit.RecordLength!.Value));
        }
        int marker = unit.Swap ? BinaryPrimitives.ReverseEndianness(data.Length) : data.Length;
        if (!unit.Stream && !unit.Direct && Stdio.Fwrite((nint)(&marker), 1, 4, unit.File) != 4) { state.Error = 1002; return; }
        fixed (byte* bytes = data)
            if (Stdio.Fwrite((nint)bytes, 1, data.Length, unit.File) != data.Length) { state.Error = 1002; return; }
        if (!unit.Stream && !unit.Direct && Stdio.Fwrite((nint)(&marker), 1, 4, unit.File) != 4) state.Error = 1002;
        unit.AfterEndfile = false;
    }

    private static void BackspaceUnformatted(Statement state, Unit unit, long position)
    {
        if (unit.Stream) { state.Error = 1002; return; }
        if (position == 0) return;
        if (position < 8 || Stdio.Fseek(unit.File, position - 4, 0) != 0) { state.Error = 1003; return; }
        int marker = 0;
        if (Stdio.Fread((nint)(&marker), 1, 4, unit.File) != 4) { state.Error = 1003; return; }
        int length = unit.Swap ? BinaryPrimitives.ReverseEndianness(marker) : marker;
        if (length < 0 || length > position - 8) { state.Error = 1003; return; }
        state.Error = Stdio.Fseek(unit.File, position - length - 8, 0);
        if (state.Error == 0) Stdio.Clearerr(unit.File);
    }
}