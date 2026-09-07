using System.Buffers.Binary;
using System.Numerics;

namespace LlvmNet.Runtime;

public static unsafe class Numeric
{
    public static int ByteSwap16(int value) => BinaryPrimitives.ReverseEndianness((ushort)value);
    public static int ByteSwap32(int value) => BinaryPrimitives.ReverseEndianness(value);
    public static long ByteSwap64(long value) => BinaryPrimitives.ReverseEndianness(value);
    public static int PopCount32(int value) => BitOperations.PopCount((uint)value);
    public static long PopCount64(long value) => BitOperations.PopCount((ulong)value);
    public static int LeadingZeros32(int value) => BitOperations.LeadingZeroCount((uint)value);
    public static long LeadingZeros64(long value) => BitOperations.LeadingZeroCount((ulong)value);
    public static int TrailingZeros32(int value) => BitOperations.TrailingZeroCount((uint)value);
    public static long TrailingZeros64(long value) => BitOperations.TrailingZeroCount((ulong)value);
    public static long CountBits(long value, int width, int operation)
    {
        ulong bits = (ulong)value & (width == 64 ? ulong.MaxValue : (1UL << width) - 1);
        return operation switch
        {
            0 => BitOperations.PopCount(bits),
            1 => BitOperations.LeadingZeroCount(bits) - (64 - width),
            2 => Math.Min(width, BitOperations.TrailingZeroCount(bits)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }
    public static long ReverseBits(long value, int width)
    {
        ulong bits = (ulong)value;
        bits = ((bits >> 1) & 0x5555555555555555UL) | ((bits & 0x5555555555555555UL) << 1);
        bits = ((bits >> 2) & 0x3333333333333333UL) | ((bits & 0x3333333333333333UL) << 2);
        bits = ((bits >> 4) & 0x0f0f0f0f0f0f0f0fUL) | ((bits & 0x0f0f0f0f0f0f0f0fUL) << 4);
        bits = BinaryPrimitives.ReverseEndianness(bits);
        return unchecked((long)(bits >> (64 - width)));
    }
    public static long Funnel(long left, long right, long count, int width, int shiftRight)
    {
        ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        ulong first = (ulong)left & mask;
        ulong second = (ulong)right & mask;
        int shift = (int)((ulong)count % (uint)width);
        if (shift == 0)
            return unchecked((long)(shiftRight != 0 ? second : first));
        ulong result = shiftRight != 0 ? (first << (width - shift)) | (second >> shift) : (first << shift) | (second >> (width - shift));
        return unchecked((long)(result & mask));
    }
    public static int FunnelLeft32(int left, int right, int count)
    {
        count &= 31;
        return count == 0 ? left : (left << count) | (int)((uint)right >> (32 - count));
    }
    public static long FunnelLeft64(long left, long right, long count)
    {
        int shift = (int)(count & 63);
        return shift == 0 ? left : (left << shift) | (long)((ulong)right >> (64 - shift));
    }
    public static int FunnelRight32(int left, int right, int count)
    {
        count &= 31;
        return count == 0 ? right : (left << (32 - count)) | (int)((uint)right >> count);
    }
    public static long FunnelRight64(long left, long right, long count)
    {
        int shift = (int)(count & 63);
        return shift == 0 ? right : (left << (64 - shift)) | (long)((ulong)right >> shift);
    }

    public static void Overflow(int operation, int signed, int width, long left, long right, nint destination, int flagOffset)
    {
        Int128 wideLeft = signed != 0 ? left : (ulong)left;
        Int128 wideRight = signed != 0 ? right : (ulong)right;
        Int128 result = operation switch
        {
            0 => wideLeft + wideRight,
            1 => wideLeft - wideRight,
            2 => wideLeft * wideRight,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        Int128 minimum = signed != 0 ? -(Int128.One << (width - 1)) : 0;
        UInt128 maximum = signed != 0 ? (UInt128.One << (width - 1)) - 1 : (UInt128.One << width) - 1;
        bool overflow = result < minimum || (result >= 0 && (UInt128)result > maximum);
        if (width <= 8)
            *(byte*)destination = (byte)result;
        else if (width <= 16)
            *(ushort*)destination = (ushort)result;
        else if (width <= 32)
            *(uint*)destination = (uint)result;
        else
            *(ulong*)destination = (ulong)result;
        *((byte*)destination + flagOffset) = overflow ? (byte)1 : (byte)0;
    }

    public static long Saturate(int subtract, int signed, int width, long left, long right)
    {
        if (signed != 0)
        {
            Int128 result = subtract != 0 ? (Int128)left - right : (Int128)left + right;
            Int128 minimum = -(Int128.One << (width - 1));
            Int128 maximum = (Int128.One << (width - 1)) - 1;
            return (long)Int128.Clamp(result, minimum, maximum);
        }
        UInt128 wideLeft = (ulong)left;
        UInt128 wideRight = (ulong)right;
        UInt128 unsignedResult = subtract != 0 ? wideLeft < wideRight ? 0 : wideLeft - wideRight : wideLeft + wideRight;
        UInt128 limit = (UInt128.One << width) - 1;
        return unchecked((long)(ulong)UInt128.Min(unsignedResult, limit));
    }

    public static long FloatToIntegerSaturated(double value, int width, int signed)
    {
        if (double.IsNaN(value)) return 0;
        if (signed != 0)
        {
            double bound = Math.ScaleB(1, width - 1);
            long minimum = width == 64 ? long.MinValue : -(1L << (width - 1));
            long maximum = width == 64 ? long.MaxValue : (1L << (width - 1)) - 1;
            return value <= -bound ? minimum : value >= bound ? maximum : (long)value;
        }
        ulong limit = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        return value <= 0 ? 0 : value >= Math.ScaleB(1, width) ? unchecked((long)limit) : unchecked((long)(ulong)value);
    }

    public static int FloatClass64(double value, int mask) => FloatClass((ulong)BitConverter.DoubleToInt64Bits(value), 52, 11, mask);
    public static int FloatClass32(float value, int mask) => FloatClass((uint)BitConverter.SingleToInt32Bits(value), 23, 8, mask);
    private static int FloatClass(ulong bits, int fractionWidth, int exponentWidth, int mask)
    {
        bool negative = (bits >> (fractionWidth + exponentWidth)) != 0;
        ulong exponentMask = (1UL << exponentWidth) - 1;
        ulong exponent = (bits >> fractionWidth) & exponentMask;
        ulong fraction = bits & ((1UL << fractionWidth) - 1);
        int classification;
        if (exponent == exponentMask)
            classification = fraction == 0 ? negative ? 4 : 512 : (fraction & (1UL << (fractionWidth - 1))) != 0 ? 2 : 1;
        else if (exponent == 0)
            classification = fraction == 0 ? negative ? 32 : 64 : negative ? 16 : 128;
        else
            classification = negative ? 8 : 256;
        return (classification & mask) != 0 ? 1 : 0;
    }
}