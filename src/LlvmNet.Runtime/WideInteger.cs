using System.Numerics;
using System.Runtime.CompilerServices;

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

    // The compiler passes every width, opcode and predicate below as a constant, and translated C++ loads, stores and
    // masks 65- to 128-bit and odd-width integers in hot code (LLVM merges adjacent bit-fields and small aggregates into
    // one integer: Clang's AST node constructors, hashing), so these are inlined and written on the two 64-bit halves:
    // RyuJIT then folds the tests of the constants and keeps the halves in registers, where calls of UInt128's operators
    // with a variable shift stayed calls (PF-30).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong High(UInt128 value) => (ulong)(value >> 64);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt128 Mask(UInt128 value, int width)
    {
        if (width >= 128) return value;
        if (width > 64) return new UInt128(High(value) & (ulong.MaxValue >> (128 - width)), (ulong)value);
        return width == 64 ? (ulong)value : (ulong)value & ((1UL << width) - 1);
    }

    /// <summary>The value with the bits above its width set to its sign bit; the value is within its width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt128 ExtendSign(UInt128 value, int width)
    {
        if (width >= 128) return value;
        if (width > 64) return new UInt128((ulong)((long)(High(value) << (128 - width)) >> (128 - width)), (ulong)value);
        return (value & (UInt128.One << (width - 1))) == 0 ? value : value | ~((UInt128.One << width) - 1);
    }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt128 Binary(UInt128 left, UInt128 right, int opcode, int width)
    {
        switch (opcode)
        {
            case 8:
            {
                ulong low = (ulong)left + (ulong)right;
                return Mask(new UInt128(High(left) + High(right) + (low < (ulong)left ? 1UL : 0UL), low), width);
            }
            case 10:
                return Mask(new UInt128(High(left) - High(right) - ((ulong)left < (ulong)right ? 1UL : 0UL), (ulong)left - (ulong)right), width);
            case 12: return Mask(unchecked(left * right), width);
            case 14: return Mask(left / right, width);
            case 15: return Mask(unchecked((UInt128)((Int128)ExtendSign(left, width) / (Int128)ExtendSign(right, width))), width);
            case 17: return Mask(left % right, width);
            case 18: return Mask(unchecked((UInt128)((Int128)ExtendSign(left, width) % (Int128)ExtendSign(right, width))), width);
            case 20: return Mask(left << ShiftAmount(right, width), width);
            case 21: return Mask(left >> ShiftAmount(right, width), width);
            case 22: return Mask(unchecked((UInt128)((Int128)ExtendSign(left, width) >> ShiftAmount(right, width))), width);
            case 23: return Mask(new UInt128(High(left) & High(right), (ulong)left & (ulong)right), width);
            case 24: return Mask(new UInt128(High(left) | High(right), (ulong)left | (ulong)right), width);
            case 25: return Mask(new UInt128(High(left) ^ High(right), (ulong)left ^ (ulong)right), width);
            default: throw new NotSupportedException($"Wide integer opcode {opcode}");
        }
    }

    private static int ShiftAmount(UInt128 amount, int width)
    {
        if (amount >= (uint)width) throw new ArithmeticException("Undefined oversized LLVM integer shift.");
        return (int)(ulong)amount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare(UInt128 left, UInt128 right, int predicate, int width)
    {
        // Signed order is unsigned order of the sign-extended values with their sign bits flipped.
        if (predicate is >= 38 and <= 41)
        {
            left = ExtendSign(left, width) ^ SignBit;
            right = ExtendSign(right, width) ^ SignBit;
            predicate -= 4;
        }
        ulong leftHigh = High(left), rightHigh = High(right), leftLow = (ulong)left, rightLow = (ulong)right;
        bool result = predicate switch
        {
            32 => leftHigh == rightHigh && leftLow == rightLow,
            33 => leftHigh != rightHigh || leftLow != rightLow,
            34 => leftHigh > rightHigh || leftHigh == rightHigh && leftLow > rightLow,
            35 => leftHigh > rightHigh || leftHigh == rightHigh && leftLow >= rightLow,
            36 => leftHigh < rightHigh || leftHigh == rightHigh && leftLow < rightLow,
            37 => leftHigh < rightHigh || leftHigh == rightHigh && leftLow <= rightLow,
            _ => throw new NotSupportedException($"Wide integer comparison {predicate}")
        };
        return result ? 1 : 0;
    }

    private static UInt128 SignBit => new(1UL << 63, 0);

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
    /// <summary>The integer in the first 1 to 8 bytes at an address, with two loads that overlap when the count is not a power of two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadBytes(byte* address, int count) => count switch
    {
        1 => *address,
        2 => Unsafe.ReadUnaligned<ushort>(address),
        3 => Unsafe.ReadUnaligned<ushort>(address) | (ulong)address[2] << 16,
        4 => Unsafe.ReadUnaligned<uint>(address),
        5 or 6 or 7 => Unsafe.ReadUnaligned<uint>(address) | (ulong)Unsafe.ReadUnaligned<uint>(address + count - 4) << ((count - 4) * 8),
        _ => Unsafe.ReadUnaligned<ulong>(address)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteBytes(byte* address, ulong value, int count)
    {
        switch (count)
        {
            case 1: *address = (byte)value; break;
            case 2: Unsafe.WriteUnaligned(address, (ushort)value); break;
            case 3:
                Unsafe.WriteUnaligned(address, (ushort)value);
                address[2] = (byte)(value >> 16);
                break;
            case 4: Unsafe.WriteUnaligned(address, (uint)value); break;
            case 5 or 6 or 7:
                Unsafe.WriteUnaligned(address, (uint)value);
                Unsafe.WriteUnaligned(address + count - 4, (uint)(value >> ((count - 4) * 8)));
                break;
            default: Unsafe.WriteUnaligned(address, value); break;
        }
    }

    /// <summary>A load of an integer of 1 to 128 bits: the (width + 7) / 8 bytes at the address, little-endian, within the width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt128 Read(nint address, int width)
    {
        int count = (width + 7) / 8;
        if (count > 8) return Mask(new UInt128(ReadBytes((byte*)address + 8, count - 8), Unsafe.ReadUnaligned<ulong>((byte*)address)), width);
        return count == 0 ? UInt128.Zero : Mask(ReadBytes((byte*)address, count), width);
    }

    /// <summary>A store of an integer of 1 to 128 bits: its low (width + 7) / 8 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(nint address, UInt128 value, int width)
    {
        int count = (width + 7) / 8;
        if (count > 8)
        {
            Unsafe.WriteUnaligned((byte*)address, (ulong)value);
            WriteBytes((byte*)address + 8, High(value), count - 8);
        }
        else if (count != 0)
            WriteBytes((byte*)address, (ulong)value, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadNarrow(nint address, int width)
    {
        int count = (width + 7) / 8;
        if (count == 0) return 0;
        ulong value = ReadBytes((byte*)address, count);
        return unchecked((long)(width >= 64 ? value : value & ((1UL << width) - 1)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNarrow(nint address, long value, int width)
    {
        int count = (width + 7) / 8;
        if (count != 0) WriteBytes((byte*)address, unchecked((ulong)value), count);
    }

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
