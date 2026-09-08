namespace LlvmNet.Runtime;

public static unsafe class Fortran
{
    [CExport("_FortranASystemClockCount")]
    public static long SystemClockCount(int kind)
    {
        ulong maximum = (ulong)SystemClockCountMax(kind);
        UInt128 count = (UInt128)(ulong)System.Diagnostics.Stopwatch.GetTimestamp() * (ulong)SystemClockCountRate(kind) /
            (ulong)System.Diagnostics.Stopwatch.Frequency;
        return (long)(count & maximum);
    }
    [CExport("_FortranASystemClockCountRate")]
    public static long SystemClockCountRate(int kind) => kind >= 8 ? 1000000000 : kind >= 2 ? 1000 : 10;
    [CExport("_FortranASystemClockCountMax")]
    public static long SystemClockCountMax(int kind) => kind switch
    {
        1 => sbyte.MaxValue, 2 => short.MaxValue, 4 => int.MaxValue, 8 or 16 => long.MaxValue,
        _ => throw new NotSupportedException($"SYSTEM_CLOCK kind {kind} is not implemented.")
    };
    [CExport("_FortranACpuTime")]
    public static double CpuTime() => System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;

    [CExport("_FortranADateAndTime")]
    public static void DateAndTime(nint date, long dateLength, nint time, long timeLength, nint zone, long zoneLength, nint source, int line, nint values)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        int offset = checked((int)now.Offset.TotalMinutes);
        Write(date, dateLength, now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        Write(time, timeLength, now.ToString("HHmmss.fff", System.Globalization.CultureInfo.InvariantCulture));
        Write(zone, zoneLength, $"{(offset < 0 ? "-" : "+")}{Math.Abs(offset) / 60:00}{Math.Abs(offset) % 60:00}");
        if (values == 0) return;
        var array = new FortranDescriptor(values);
        if (array.Rank != 1 || array.Elements < 8)
            throw new InvalidOperationException("DATE_AND_TIME VALUES requires at least eight integer elements.");
        int[] components = [now.Year, now.Month, now.Day, offset, now.Hour, now.Minute, now.Second, now.Millisecond];
        for (int index = 0; index < components.Length; index++)
        {
            nint address = array.Address(index);
            switch (array.ElementBytes)
            {
                case 2: *(short*)address = checked((short)components[index]); break;
                case 4: *(int*)address = components[index]; break;
                case 8: *(long*)address = components[index]; break;
                default: throw new NotSupportedException("DATE_AND_TIME integer kind is not implemented.");
            }
        }

        static void Write(nint destination, long length, string value)
        {
            if (destination == 0) return;
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
            System.Runtime.InteropServices.NativeMemory.Fill((void*)destination, checked((nuint)length), (byte)' ');
            bytes.AsSpan(0, checked((int)Math.Min(length, bytes.Length))).CopyTo(new Span<byte>((void*)destination, checked((int)length)));
        }
    }

    [CExport("_FortranAModReal8")]
    public static double ModReal8(double value, double divisor, nint source, int line)
    {
        if (divisor == 0) throw new InvalidOperationException($"Fortran MOD divisor is zero at line {line}.");
        return value % divisor;
    }
    [CExport("_FortranAModReal4")]
    public static float ModReal4(float value, float divisor, nint source, int line) => (float)ModReal8(value, divisor, source, line);
    [CExport("_FortranAModuloReal8")]
    public static double ModuloReal8(double value, double divisor, nint source, int line)
    {
        double remainder = ModReal8(value, divisor, source, line);
        if (remainder == 0) return Math.CopySign(0, divisor);
        return (remainder < 0) != (divisor < 0) ? remainder + divisor : remainder;
    }
    [CExport("_FortranAModuloReal4")]
    public static float ModuloReal4(float value, float divisor, nint source, int line) => (float)ModuloReal8(value, divisor, source, line);

    [CExport("_FortranAExponent8_4")]
    public static int ExponentReal8(double value) => value == 0 ? 0 : double.IsFinite(value) ? Math.ILogB(value) + 1 : int.MaxValue;
    [CExport("_FortranAScale8")]
    public static double ScaleReal8(double value, long exponent) => Math.ScaleB(value, (int)Math.Clamp(exponent, int.MinValue, int.MaxValue));
    [CExport("_FortranAFraction8")]
    public static double FractionReal8(double value) => value == 0 ? value : double.IsFinite(value) ? Math.ScaleB(value, -ExponentReal8(value)) : double.NaN;
    [CExport("_FortranASetExponent8")]
    public static double SetExponentReal8(double value, long exponent) => ScaleReal8(FractionReal8(value), exponent);

    [CExport("_FortranASize")]
    public static long ArraySize(nint array, nint source, int line) => new FortranDescriptor(array).Elements;

    [CExport("_FortranASizeDim")]
    public static long SizeDimension(nint array, int dimension, nint source, int line)
    {
        var descriptor = new FortranDescriptor(array);
        if (dimension < 1 || dimension > descriptor.Rank)
            throw new ArgumentOutOfRangeException(nameof(dimension), "Fortran SIZE dimension must be within the array rank.");
        return Math.Max(0, descriptor.Extent(dimension - 1));
    }

    [CExport("_FortranAPointerIsAssociatedWith")]
    public static int PointerAssociatedWith(nint pointer, nint target)
    {
        if (pointer == 0 || target == 0) return 0;
        var first = new FortranDescriptor(pointer);
        var second = new FortranDescriptor(target);
        if (first.Base == 0 || first.Base != second.Base || first.ElementBytes == 0 || first.ElementBytes != second.ElementBytes || first.Rank != second.Rank)
            return 0;
        for (int dimension = 0; dimension < first.Rank; dimension++)
        {
            long extent = first.Extent(dimension);
            if (extent <= 0 || extent != second.Extent(dimension) || extent > 1 && first.Stride(dimension) != second.Stride(dimension)) return 0;
        }
        return 1;
    }

    [CExport("_FortranAIsContiguous")]
    public static int IsContiguous(nint array)
    {
        var descriptor = new FortranDescriptor(array);
        if (descriptor.Elements == 0) return 1;
        long stride = descriptor.ElementBytes;
        for (int dimension = 0; dimension < descriptor.Rank; dimension++)
        {
            long extent = descriptor.Extent(dimension);
            if (extent > 1 && descriptor.Stride(dimension) != stride) return 0;
            stride = checked(stride * extent);
        }
        return 1;
    }

    [CExport("_FortranAIndex1")]
    public static long Index1(nint text, long length, nint substring, long substringLength, int back)
    {
        var characters = new ReadOnlySpan<byte>((void*)text, checked((int)length));
        var pattern = new ReadOnlySpan<byte>((void*)substring, checked((int)substringLength));
        return (back != 0 ? characters.LastIndexOf(pattern) : characters.IndexOf(pattern)) + 1L;
    }

    [CExport("_FortranAScan1")]
    public static long Scan1(nint text, long length, nint set, long setLength, int back) => SearchSet(text, length, set, setLength, back, false);
    [CExport("_FortranAVerify1")]
    public static long Verify1(nint text, long length, nint set, long setLength, int back) => SearchSet(text, length, set, setLength, back, true);
    private static long SearchSet(nint text, long length, nint set, long setLength, int back, bool invert)
    {
        var characters = new ReadOnlySpan<byte>((void*)text, checked((int)length));
        var members = new ReadOnlySpan<byte>((void*)set, checked((int)setLength));
        for (int position = 0; position < characters.Length; position++)
        {
            int index = back == 0 ? position : characters.Length - position - 1;
            if (members.Contains(characters[index]) != invert) return index + 1L;
        }
        return 0;
    }

    [CExport("_FortranAArgumentCount")]
    public static int ArgumentCount() => ProcessRuntime.ArgumentCount - 1;
    [CExport("_FortranAGetEnvVariable")]
    public static int GetEnvironmentVariable(nint name, nint value, nint length, int trimName, nint errorMessage, nint source, int line)
    {
        var identifier = new FortranDescriptor(name);
        if (identifier.Type != 40 || identifier.Rank != 0)
            throw new InvalidOperationException("Fortran environment variable name must be scalar CHARACTER(kind=1).");
        string key = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)identifier.Base, checked((int)identifier.ElementBytes)));
        if (trimName != 0) key = key.TrimEnd(' ');
        string? text = key.Length == 0 || key.Contains('=') || key.Contains('\0') ? null : Environment.GetEnvironmentVariable(key);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text ?? "");
        if (length != 0)
        {
            var size = new FortranDescriptor(length);
            if (size.ElementBytes == 4) *(int*)size.Base = bytes.Length;
            else if (size.ElementBytes == 8) *(long*)size.Base = bytes.Length;
            else throw new NotSupportedException("Fortran environment variable length kind.");
        }
        int result = text is null ? 1 : 0;
        if (value != 0)
        {
            var output = new FortranDescriptor(value);
            if (output.Type != 40 || output.Rank != 0)
                throw new InvalidOperationException("Fortran environment variable value must be scalar CHARACTER(kind=1).");
            Write(output, bytes);
            if (text is not null && bytes.Length > output.ElementBytes) result = -1;
        }
        if (result != 0 && errorMessage != 0)
            Write(new FortranDescriptor(errorMessage), System.Text.Encoding.UTF8.GetBytes(text is null ? "Environment variable not found" : "Environment variable value truncated"));
        return result;

        static void Write(FortranDescriptor output, byte[] bytes)
        {
            System.Runtime.InteropServices.NativeMemory.Fill((void*)output.Base, checked((nuint)output.ElementBytes), (byte)' ');
            bytes.AsSpan(0, checked((int)Math.Min(bytes.Length, output.ElementBytes))).CopyTo(new Span<byte>((void*)output.Base, checked((int)output.ElementBytes)));
        }
    }
    [CExport("_FortranAGetCommandArgument")]
    public static int GetCommandArgument(int index, nint argument, nint length, nint errorMessage, nint source, int line)
    {
        bool valid = index >= 0 && index < ProcessRuntime.ArgumentCount;
        nint text = valid ? ((nint*)ProcessRuntime.Arguments)[index] : 0;
        long count = valid ? CString.Strlen(text) : 0;
        if (length != 0)
        {
            var size = new FortranDescriptor(length);
            if (size.ElementBytes == 4) *(int*)size.Base = checked((int)count);
            else if (size.ElementBytes == 8) *(long*)size.Base = count;
            else throw new NotSupportedException("Fortran command argument length kind.");
        }
        int result = valid ? 0 : 1;
        if (argument != 0)
        {
            var output = new FortranDescriptor(argument);
            if (output.Type != 40 || output.Rank != 0) throw new InvalidOperationException("Fortran command argument must be scalar CHARACTER(kind=1).");
            System.Runtime.InteropServices.NativeMemory.Fill((void*)output.Base, (nuint)output.ElementBytes, (byte)' ');
            if (count > 0) Memory.Copy(output.Base, text, Math.Min(count, output.ElementBytes));
            if (valid && count > output.ElementBytes) result = -1;
        }
        if (result != 0 && errorMessage != 0)
        {
            var message = new FortranDescriptor(errorMessage);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(valid ? "Argument value truncated" : "Invalid argument number");
            System.Runtime.InteropServices.NativeMemory.Fill((void*)message.Base, (nuint)message.ElementBytes, (byte)' ');
            bytes.AsSpan(0, (int)Math.Min(bytes.Length, message.ElementBytes)).CopyTo(new Span<byte>((void*)message.Base, checked((int)message.ElementBytes)));
        }
        return result;
    }

    [CExport("_FortranAProgramStart")]
    public static void ProgramStart(int count, nint arguments, nint environment, nint defaults)
    {
        if (count != ProcessRuntime.ArgumentCount || arguments != ProcessRuntime.Arguments)
            throw new InvalidOperationException("Fortran startup arguments do not match the managed process entry point.");
        string? conversion = Environment.GetEnvironmentVariable("FORT_CONVERT");
        if (defaults != 0)
        {
            int size = *(int*)defaults;
            nint entries = *(nint*)(defaults + 8);
            if (size < 0 || size > 1024 || size != 0 && entries == 0)
                throw new InvalidOperationException("Invalid Fortran environment defaults.");
            for (int index = 0; index < size; index++)
            {
                nint entry = entries + index * 16;
                string name = CString.Text(*(nint*)entry);
                if (name != "FORT_CONVERT") throw new NotSupportedException($"Fortran environment default {name} is not implemented.");
                conversion ??= CString.Text(*(nint*)(entry + 8));
            }
        }
        FortranIO.SetDefaultConversion(conversion ?? "native");
    }
    [CExport("_FortranASumReal8")]
    public static double SumReal8(nint array, nint source, int line, int dimension, nint mask)
    {
        var descriptor = new FortranDescriptor(array);
        CheckReduction(descriptor, dimension, 8);
        double sum = 0;
        for (long index = 0; index < descriptor.Elements; index++)
            if (mask == 0 || new FortranDescriptor(mask).Logical(index))
                sum += *(double*)descriptor.Address(index);
        return sum;
    }
    [CExport("_FortranASumReal4")]
    public static float SumReal4(nint array, nint source, int line, int dimension, nint mask)
    {
        var descriptor = new FortranDescriptor(array);
        CheckReduction(descriptor, dimension, 4);
        float sum = 0;
        for (long index = 0; index < descriptor.Elements; index++)
            if (mask == 0 || new FortranDescriptor(mask).Logical(index))
                sum += *(float*)descriptor.Address(index);
        return sum;
    }
    [CExport("_FortranASumInteger4")]
    public static int SumInteger4(nint array, nint source, int line, int dimension, nint mask)
    {
        var descriptor = new FortranDescriptor(array);
        CheckReduction(descriptor, dimension, 4);
        int sum = 0;
        for (long index = 0; index < descriptor.Elements; index++)
            if (mask == 0 || new FortranDescriptor(mask).Logical(index))
                sum = unchecked(sum + *(int*)descriptor.Address(index));
        return sum;
    }
    [CExport("_FortranASumInteger8")]
    public static long SumInteger8(nint array, nint source, int line, int dimension, nint mask)
    {
        var descriptor = new FortranDescriptor(array);
        CheckReduction(descriptor, dimension, 8);
        long sum = 0;
        for (long index = 0; index < descriptor.Elements; index++)
            if (mask == 0 || new FortranDescriptor(mask).Logical(index))
                sum = unchecked(sum + *(long*)descriptor.Address(index));
        return sum;
    }
    private static void CheckReduction(FortranDescriptor descriptor, int dimension, int bytes)
    {
        if (descriptor.ElementBytes != bytes || dimension != 0 && (descriptor.Rank != 1 || dimension != 1))
            throw new InvalidOperationException("Invalid scalar Fortran reduction descriptor or dimension.");
    }

    [CExport("_FortranAStopStatement")]
    public static void Stop(int code, int error, int quiet)
    {
        if (quiet == 0)
            Console.Error.WriteLine($"{(error != 0 ? "ERROR STOP" : "STOP")} {code}");
        ProcessRuntime.Exit(code);
    }
    [CExport("_FortranAStopStatementText")]
    public static unsafe void StopText(nint text, long length, int error, int quiet)
    {
        if (quiet == 0)
            Console.Error.WriteLine($"{(error != 0 ? "ERROR STOP" : "STOP")} {System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)text, checked((int)length)))}");
        ProcessRuntime.Exit(error != 0 ? 1 : 0);
    }
    [CExport("_FortranAProgramEndStatement")]
    public static void EndProgram() => ProcessRuntime.Exit(0);
}