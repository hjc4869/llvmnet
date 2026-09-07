namespace LlvmNet.Runtime;

public static unsafe class Atomics
{
    private static readonly object gate = new();

    public static long Load(nint address, int width)
    {
        lock (gate)
            return Read(address, width);
    }

    public static void Store(nint address, long value, int width)
    {
        lock (gate)
            Write(address, value, width);
    }

    public static long CompareExchange(nint address, long replacement, long expected, int width)
    {
        lock (gate)
        {
            long previous = Read(address, width);
            if (Mask(previous, width) == Mask(expected, width))
                Write(address, replacement, width);
            return previous;
        }
    }

    public static long Modify(nint address, long operand, int operation, int width)
    {
        lock (gate)
        {
            long previous = Read(address, width);
            long result = operation switch
            {
                0 => operand,
                1 => unchecked(previous + operand),
                2 => unchecked(previous - operand),
                3 => previous & operand,
                4 => ~(previous & operand),
                5 => previous | operand,
                6 => previous ^ operand,
                7 => Math.Max(Signed(previous, width), Signed(operand, width)),
                8 => Math.Min(Signed(previous, width), Signed(operand, width)),
                9 => unchecked((long)Math.Max(Mask(previous, width), Mask(operand, width))),
                10 => unchecked((long)Math.Min(Mask(previous, width), Mask(operand, width))),
                _ => throw new NotSupportedException($"Atomic RMW operation {operation}")
            };
            Write(address, result, width);
            return previous;
        }
    }

    private static ulong Mask(long value, int width) => (ulong)value & (width == 64 ? ulong.MaxValue : (1UL << width) - 1);
    private static long Signed(long value, int width) => width == 64 ? value : value << (64 - width) >> (64 - width);
    private static long Read(nint address, int width) => width switch
    {
        8 => *(byte*)address,
        16 => *(ushort*)address,
        32 => *(uint*)address,
        64 => *(long*)address,
        _ => throw new NotSupportedException($"Atomic width {width}")
    };
    private static void Write(nint address, long value, int width)
    {
        switch (width)
        {
            case 8: *(byte*)address = (byte)value; break;
            case 16: *(ushort*)address = (ushort)value; break;
            case 32: *(uint*)address = (uint)value; break;
            case 64: *(long*)address = value; break;
            default: throw new NotSupportedException($"Atomic width {width}");
        }
    }
}