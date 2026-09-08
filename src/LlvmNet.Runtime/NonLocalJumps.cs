namespace LlvmNet.Runtime;

public sealed class NonLocalJumpException(long frame, int site, int value, nint stackPosition) : Exception
{
    public long Frame { get; } = frame;
    public int Site { get; } = site;
    public int Value { get; } = value;
    public nint StackPosition { get; } = stackPosition;
}

public static unsafe class NonLocalJumps
{
    private static long nextFrame;

    public static long NewFrame() => Interlocked.Increment(ref nextFrame);

    public static int Save(nint buffer, long frame, int site, int saveMask, int system, nint stackPosition)
    {
        if (saveMask != 0 && system == 0)
            throw new NotSupportedException("Saving signal masks with sigsetjmp is not implemented for the portable ABI.");
        *(long*)buffer = frame;
        *(int*)(buffer + 8) = site;
        *(int*)(buffer + 12) = saveMask != 0 && system != 0 ? 1 : 0;
        *(nint*)(buffer + 16) = stackPosition;
        if (*(int*)(buffer + 12) != 0)
        {
            var signalMask = (delegate* unmanaged[Cdecl]<int, nint, nint, int>)SystemAbi.Symbol("libc.so.6", "pthread_sigmask");
            if (signalMask(0, 0, buffer + 24) != 0)
                throw new InvalidOperationException("Cannot save the signal mask for sigsetjmp.");
        }
        return 0;
    }

    [CExport("longjmp"), CExport("_longjmp"), CExport("siglongjmp"), CExport("__longjmp_chk")]
    public static void Jump(nint buffer, int value)
    {
        long frame = *(long*)buffer;
        int site = *(int*)(buffer + 8);
        nint stackPosition = *(nint*)(buffer + 16);
        if (*(int*)(buffer + 12) != 0)
        {
            var signalMask = (delegate* unmanaged[Cdecl]<int, nint, nint, int>)SystemAbi.Symbol("libc.so.6", "pthread_sigmask");
            if (signalMask(2, buffer + 24, 0) != 0)
                throw new InvalidOperationException("Cannot restore the signal mask for siglongjmp.");
        }
        throw new NonLocalJumpException(frame, site, value == 0 ? 1 : value, stackPosition);
    }
}