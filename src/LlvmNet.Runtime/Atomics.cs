namespace LlvmNet.Runtime;

/// <summary>
/// LLVM atomic load/store/cmpxchg/atomicrmw. Naturally aligned 8- to 64-bit locations use hardware atomics through
/// Interlocked and Volatile, so they are lock-free and interoperate with native code's atomics in system mode; stores
/// use Interlocked.Exchange, which is sequentially consistent and so valid for every LLVM ordering. A misaligned
/// location always takes the locked path, so every access to a given address is consistent.
/// </summary>
public static unsafe class Atomics
{
    private static readonly object misaligned = new();

    private static bool Aligned(nint address, int width) => (address & (width / 8 - 1)) == 0;

    public static long Load(nint address, int width)
    {
        if (!Aligned(address, width))
            lock (misaligned) return Read(address, width);
        return width switch
        {
            8 => Volatile.Read(ref *(byte*)address),
            16 => Volatile.Read(ref *(ushort*)address),
            32 => Volatile.Read(ref *(uint*)address),
            64 => Volatile.Read(ref *(long*)address),
            _ => throw new NotSupportedException($"Atomic width {width}")
        };
    }

    public static void Store(nint address, long value, int width)
    {
        if (!Aligned(address, width))
        {
            lock (misaligned) Write(address, value, width);
            return;
        }
        Exchange(address, value, width);
    }

    public static long CompareExchange(nint address, long replacement, long expected, int width)
    {
        if (!Aligned(address, width))
            lock (misaligned)
            {
                long previous = Read(address, width);
                if (Mask(previous, width) == Mask(expected, width)) Write(address, replacement, width);
                return previous;
            }
        return Swap(address, replacement, expected, width);
    }

    public static long Modify(nint address, long operand, int operation, int width)
    {
        if (!Aligned(address, width))
            lock (misaligned)
            {
                long previous = Read(address, width);
                Write(address, Apply(previous, operand, operation, width), width);
                return previous;
            }
        switch (operation)
        {
            case 0: return Exchange(address, operand, width);
            case 1 when width == 32: return (uint)(Interlocked.Add(ref *(int*)address, (int)operand) - (int)operand);
            case 1 when width == 64: return Interlocked.Add(ref *(long*)address, operand) - operand;
            case 3 when width == 32: return Interlocked.And(ref *(uint*)address, (uint)operand);
            case 3 when width == 64: return Interlocked.And(ref *(long*)address, operand);
            case 5 when width == 32: return Interlocked.Or(ref *(uint*)address, (uint)operand);
            case 5 when width == 64: return Interlocked.Or(ref *(long*)address, operand);
        }
        long current = Load(address, width);
        while (true)
        {
            long observed = Swap(address, Apply(current, operand, operation, width), current, width);
            if (Mask(observed, width) == Mask(current, width)) return observed;
            current = observed;
        }
    }

    public static float ModifySingle(nint address, float operand, int operation)
    {
        if (!Aligned(address, 32))
            lock (misaligned)
            {
                float previous = *(float*)address;
                *(float*)address = ApplyFloating(previous, operand, operation);
                return previous;
            }
        int current = Volatile.Read(ref *(int*)address);
        while (true)
        {
            float value = BitConverter.Int32BitsToSingle(current);
            int replacement = BitConverter.SingleToInt32Bits(ApplyFloating(value, operand, operation));
            int observed = Interlocked.CompareExchange(ref *(int*)address, replacement, current);
            if (observed == current) return value;
            current = observed;
        }
    }

    public static double ModifyDouble(nint address, double operand, int operation)
    {
        if (!Aligned(address, 64))
            lock (misaligned)
            {
                double previous = *(double*)address;
                *(double*)address = ApplyFloating(previous, operand, operation);
                return previous;
            }
        long current = Volatile.Read(ref *(long*)address);
        while (true)
        {
            double value = BitConverter.Int64BitsToDouble(current);
            long replacement = BitConverter.DoubleToInt64Bits(ApplyFloating(value, operand, operation));
            long observed = Interlocked.CompareExchange(ref *(long*)address, replacement, current);
            if (observed == current) return value;
            current = observed;
        }
    }

    // 128-bit atomics: .NET exposes no 16-byte compare-exchange, so these always take a lock striped by
    // address. They are atomic with respect to each other, not to native cmpxchg16b in system-mode libraries.
    private static readonly object[] wideStripes = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private static object Stripe(nint address) => wideStripes[(int)((ulong)address >> 4 & 63)];

    public static UInt128 Load128(nint address)
    {
        lock (Stripe(address)) return *(UInt128*)address;
    }

    public static void Store128(nint address, UInt128 value)
    {
        lock (Stripe(address)) *(UInt128*)address = value;
    }

    public static UInt128 CompareExchange128(nint address, UInt128 replacement, UInt128 expected)
    {
        lock (Stripe(address))
        {
            UInt128 previous = *(UInt128*)address;
            if (previous == expected) *(UInt128*)address = replacement;
            return previous;
        }
    }

    public static UInt128 Modify128(nint address, UInt128 operand, int operation)
    {
        lock (Stripe(address))
        {
            UInt128 previous = *(UInt128*)address;
            *(UInt128*)address = operation switch
            {
                0 => operand,
                1 => previous + operand,
                2 => previous - operand,
                3 => previous & operand,
                4 => ~(previous & operand),
                5 => previous | operand,
                6 => previous ^ operand,
                7 => (Int128)previous >= (Int128)operand ? previous : operand,
                8 => (Int128)previous <= (Int128)operand ? previous : operand,
                9 => previous >= operand ? previous : operand,
                10 => previous <= operand ? previous : operand,
                _ => throw new NotSupportedException($"Atomic RMW operation {operation}")
            };
            return previous;
        }
    }

    private static long Exchange(nint address, long value, int width) => width switch
    {
        8 => Interlocked.Exchange(ref *(byte*)address, (byte)value),
        16 => Interlocked.Exchange(ref *(ushort*)address, (ushort)value),
        32 => Interlocked.Exchange(ref *(uint*)address, (uint)value),
        64 => Interlocked.Exchange(ref *(long*)address, value),
        _ => throw new NotSupportedException($"Atomic width {width}")
    };

    private static long Swap(nint address, long replacement, long expected, int width) => width switch
    {
        8 => Interlocked.CompareExchange(ref *(byte*)address, (byte)replacement, (byte)expected),
        16 => Interlocked.CompareExchange(ref *(ushort*)address, (ushort)replacement, (ushort)expected),
        32 => Interlocked.CompareExchange(ref *(uint*)address, (uint)replacement, (uint)expected),
        64 => Interlocked.CompareExchange(ref *(long*)address, replacement, expected),
        _ => throw new NotSupportedException($"Atomic width {width}")
    };

    private static long Apply(long previous, long operand, int operation, int width) => operation switch
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

    private static float ApplyFloating(float previous, float operand, int operation) => operation switch
    {
        0 => operand,
        11 => previous + operand,
        12 => previous - operand,
        13 => float.MaxNumber(previous, operand),
        14 => float.MinNumber(previous, operand),
        _ => throw new NotSupportedException($"Floating atomic RMW operation {operation}")
    };

    private static double ApplyFloating(double previous, double operand, int operation) => operation switch
    {
        0 => operand,
        11 => previous + operand,
        12 => previous - operand,
        13 => double.MaxNumber(previous, operand),
        14 => double.MinNumber(previous, operand),
        _ => throw new NotSupportedException($"Floating atomic RMW operation {operation}")
    };

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
