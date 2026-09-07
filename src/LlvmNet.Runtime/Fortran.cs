namespace LlvmNet.Runtime;

public static unsafe class Fortran
{
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

    [CExport("_FortranAArgumentCount")]
    public static int ArgumentCount() => ProcessRuntime.ArgumentCount - 1;
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
        if (defaults != 0)
            throw new NotSupportedException("Fortran compiler-provided environment defaults are not implemented.");
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