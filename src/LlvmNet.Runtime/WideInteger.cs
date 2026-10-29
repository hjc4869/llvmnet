using System.Numerics;

namespace LlvmNet.Runtime;

public static unsafe class WideInteger
{
    public static UInt128 ByteSwap(UInt128 value, int width)
    {
        UInt128 result = 0;
        for (int offset = 0; offset < width; offset += 8)
        {
            result = (result << 8) | (value & 255);
            value >>= 8;
        }
        return result;
    }

    public static UInt128 FromBits(long low, long high) => new(unchecked((ulong)high), unchecked((ulong)low));
    public static UInt128 FromUnsigned(long value) => unchecked((ulong)value);
    public static UInt128 FromSigned(long value) => unchecked((UInt128)(Int128)value);
    public static long Low(UInt128 value) => unchecked((long)(ulong)value);
    public static UInt128 Mask(UInt128 value, int width) => width == 128 ? value : value & ((UInt128.One << width) - 1);
    public static UInt128 ExtendSign(UInt128 value, int width) => width == 128 ? value : (value & (UInt128.One << (width - 1))) == 0 ? value : value | ~((UInt128.One << width) - 1);
    // fptosi/fptoui: out-of-range inputs are poison in LLVM and may be evaluated speculatively, so they
    // must not trap; saturate like .NET's narrow conversions do.
    public static UInt128 FromFloating(double value, int signed) => FromFloatingSaturated(value, signed, 128);
    public static double ToDouble(UInt128 value, int signed) => signed != 0 ? (double)unchecked((Int128)value) : (double)value;
    public static float ToSingle(UInt128 value, int signed) => signed != 0 ? (float)unchecked((Int128)value) : (float)value;

    public static UInt128 CountBits(UInt128 value, int width, int operation)
    {
        value = Mask(value, width);
        return operation switch
        {
            0 => UInt128.PopCount(value),
            1 => UInt128.LeadingZeroCount(value) - (uint)(128 - width),
            2 => UInt128.Min(UInt128.TrailingZeroCount(value), (uint)width),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    /// <summary>
    /// Integer intrinsics on 65- to 128-bit values. Codes: 0 abs, 1 smin, 2 smax, 3 umin, 4 umax, 5 bitreverse,
    /// 6 fshl, 7 fshr, 8 sadd.sat, 9 uadd.sat, 10 ssub.sat, 11 usub.sat. Results are masked to the width.
    /// </summary>
    public static UInt128 Operation(UInt128 a, UInt128 b, UInt128 c, int code, int width)
    {
        a = Mask(a, width);
        b = Mask(b, width);
        Int128 sa = unchecked((Int128)ExtendSign(a, width)), sb = unchecked((Int128)ExtendSign(b, width));
        UInt128 umax = Mask(UInt128.MaxValue, width);
        Int128 smax = unchecked((Int128)(umax >> 1)), smin = -smax - 1;
        UInt128 result = code switch
        {
            0 => sa < 0 ? unchecked((UInt128)(-sa)) : a,
            1 => sa <= sb ? a : b,
            2 => sa >= sb ? a : b,
            3 => a <= b ? a : b,
            4 => a >= b ? a : b,
            5 => Reverse(a, width),
            6 or 7 => Funnel(a, b, c % (UInt128)width, width, code == 7),
            8 => unchecked((UInt128)SaturatingSigned(sa, sb, false, smin, smax)),
            9 => a + b < a || a + b > umax ? umax : a + b,
            10 => unchecked((UInt128)SaturatingSigned(sa, sb, true, smin, smax)),
            11 => a < b ? 0 : a - b,
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
        return Mask(result, width);
    }

    private static UInt128 Reverse(UInt128 value, int width)
    {
        UInt128 result = 0;
        for (int bit = 0; bit < width; bit++)
            if ((value >> bit & 1) != 0) result |= UInt128.One << (width - 1 - bit);
        return result;
    }

    private static UInt128 Funnel(UInt128 high, UInt128 low, UInt128 amount, int width, bool right)
    {
        int shift = (int)amount;
        if (shift == 0) return right ? low : high;
        return right ? Mask(low >> shift | high << (width - shift), width) : Mask(high << shift | low >> (width - shift), width);
    }

    private static Int128 SaturatingSigned(Int128 left, Int128 right, bool subtract, Int128 minimum, Int128 maximum)
    {
        // Operands are at most 128 bits wide; compute in a wider domain to detect overflow exactly.
        var exact = subtract ? (System.Numerics.BigInteger)left - right : (System.Numerics.BigInteger)left + right;
        return exact > maximum ? maximum : exact < minimum ? minimum : (Int128)exact;
    }

    /// <summary>llvm.fptosi.sat / llvm.fptoui.sat to widths above 64 bits: NaN gives 0, out-of-range values clamp.</summary>
    public static UInt128 FromFloatingSaturated(double value, int signed, int width)
    {
        if (double.IsNaN(value)) return 0;
        UInt128 umax = Mask(UInt128.MaxValue, width);
        if (signed != 0)
        {
            Int128 maximum = unchecked((Int128)(umax >> 1)), minimum = -maximum - 1;
            double truncated = Math.Truncate(value);
            if (truncated >= (double)maximum) return Mask(unchecked((UInt128)maximum), width);
            if (truncated <= (double)minimum) return Mask(unchecked((UInt128)minimum), width);
            return Mask(unchecked((UInt128)(Int128)truncated), width);
        }
        if (value <= 0) return 0;
        double whole = Math.Truncate(value);
        return whole >= (double)umax ? umax : (UInt128)whole;
    }

    public static UInt128 Binary(UInt128 left, UInt128 right, int opcode, int width)
    {
        Int128 signedLeft = unchecked((Int128)ExtendSign(left, width));
        Int128 signedRight = unchecked((Int128)ExtendSign(right, width));
        if (opcode is 20 or 21 or 22 && right >= (uint)width)
            throw new ArithmeticException("Undefined oversized LLVM integer shift.");
        UInt128 result = opcode switch
        {
            8 => unchecked(left + right),
            10 => unchecked(left - right),
            12 => unchecked(left * right),
            14 => left / right,
            15 => unchecked((UInt128)(signedLeft / signedRight)),
            17 => left % right,
            18 => unchecked((UInt128)(signedLeft % signedRight)),
            20 => left << (int)right,
            21 => left >> (int)right,
            22 => unchecked((UInt128)(signedLeft >> (int)right)),
            23 => left & right,
            24 => left | right,
            25 => left ^ right,
            _ => throw new NotSupportedException($"Wide integer opcode {opcode}")
        };
        return Mask(result, width);
    }
    public static int Compare(UInt128 left, UInt128 right, int predicate, int width)
    {
        Int128 signedLeft = unchecked((Int128)ExtendSign(left, width));
        Int128 signedRight = unchecked((Int128)ExtendSign(right, width));
        bool result = predicate switch
        {
            32 => left == right,
            33 => left != right,
            34 => left > right,
            35 => left >= right,
            36 => left < right,
            37 => left <= right,
            38 => signedLeft > signedRight,
            39 => signedLeft >= signedRight,
            40 => signedLeft < signedRight,
            41 => signedLeft <= signedRight,
            _ => throw new NotSupportedException($"Wide integer comparison {predicate}")
        };
        return result ? 1 : 0;
    }
    public static void Overflow(int operation, int signed, int width, UInt128 left, UInt128 right, nint destination, int flagOffset)
    {
        BigInteger first = signed != 0 ? (BigInteger)unchecked((Int128)ExtendSign(left, width)) : (BigInteger)left;
        BigInteger second = signed != 0 ? (BigInteger)unchecked((Int128)ExtendSign(right, width)) : (BigInteger)right;
        BigInteger result = operation switch { 0 => first + second, 1 => first - second, 2 => first * second, _ => throw new ArgumentOutOfRangeException(nameof(operation)) };
        BigInteger minimum = signed != 0 ? -(BigInteger.One << (width - 1)) : BigInteger.Zero;
        BigInteger maximum = (BigInteger.One << (signed != 0 ? width - 1 : width)) - 1;
        Write(destination, (UInt128)(result & ((BigInteger.One << width) - 1)), width);
        *((byte*)destination + flagOffset) = result < minimum || result > maximum ? (byte)1 : (byte)0;
    }
    public static UInt128 Read(nint address, int width)
    {
        UInt128 value = 0;
        for (int index = 0; index < (width + 7) / 8; index++)
            value |= (UInt128)((byte*)address)[index] << (index * 8);
        return Mask(value, width);
    }
    public static void Write(nint address, UInt128 value, int width)
    {
        for (int index = 0; index < (width + 7) / 8; index++)
            ((byte*)address)[index] = (byte)(value >> (index * 8));
    }
    public static long ReadNarrow(nint address, int width) => unchecked((long)(ulong)Read(address, width));
    public static void WriteNarrow(nint address, long value, int width) => Write(address, (ulong)value, width);

    public static UInt128 ReadPacked(nint address, long index, int width)
    {
        long offset = checked(index * width);
        UInt128 result = 0;
        for (int bit = 0; bit < width; bit++)
        {
            long position = checked(offset + bit);
            result |= (UInt128)((((byte*)address)[position / 8] >> (int)(position % 8)) & 1) << bit;
        }
        return result;
    }

    public static void WritePacked(nint address, long index, UInt128 value, int width)
    {
        long offset = checked(index * width);
        for (int bit = 0; bit < width; bit++)
        {
            long position = checked(offset + bit);
            int shift = (int)(position % 8);
            ref byte destination = ref ((byte*)address)[position / 8];
            destination = (byte)((destination & ~(1 << shift)) | ((int)((value >> bit) & 1) << shift));
        }
    }
}
