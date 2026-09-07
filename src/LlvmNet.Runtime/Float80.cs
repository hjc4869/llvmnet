using System.Numerics;
using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

[StructLayout(LayoutKind.Explicit, Size = 16)]
public readonly struct Float80
{
    [FieldOffset(0)] public readonly ulong Significand;
    [FieldOffset(8)] public readonly ushort SignExponent;

    public Float80(long significand, int signExponent)
    {
        Significand = unchecked((ulong)significand);
        SignExponent = (ushort)signExponent;
    }

    private bool Negative => (SignExponent & 0x8000) != 0;
    private int Exponent => SignExponent & 0x7fff;
    private int Scale => (Exponent == 0 ? 1 : Exponent) - 16383 - 63;
    private bool IsNan => Exponent == 0x7fff && (Significand & 0x7fffffffffffffff) != 0;
    private bool IsInfinity => Exponent == 0x7fff && !IsNan;
    private bool IsZero => Significand == 0 && Exponent == 0;
    private BigInteger Signed => Negative ? -(BigInteger)Significand : Significand;
    private static Float80 Nan => new(unchecked((long)0xc000000000000000UL), 0x7fff);
    private static Float80 Infinity(bool negative) => new(long.MinValue, (negative ? 0x8000 : 0) | 0x7fff);
    private static Float80 Zero(bool negative) => new(0, negative ? 0x8000 : 0);

    private static BigInteger RoundedRatio(BigInteger numerator, BigInteger denominator, int shift)
    {
        if (shift >= 0)
            numerator <<= shift;
        else
            denominator <<= -shift;
        BigInteger quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        int comparison = (remainder << 1).CompareTo(denominator);
        return comparison > 0 || comparison == 0 && !quotient.IsEven ? quotient + 1 : quotient;
    }

    private static Float80 Pack(BigInteger numerator, BigInteger denominator, int scale, bool negativeZero = false)
    {
        if (numerator.IsZero)
            return Zero(negativeZero);
        bool negative = numerator.Sign < 0;
        numerator = BigInteger.Abs(numerator);
        int magnitude = checked((int)(numerator.GetBitLength() - denominator.GetBitLength()));
        if (magnitude >= 0 ? numerator < denominator << magnitude : numerator << -magnitude < denominator)
            magnitude--;
        int exponent = magnitude + scale;
        if (exponent > 16383)
            return Infinity(negative);
        int storedScale = Math.Max(exponent, -16382) - 63;
        BigInteger significand = RoundedRatio(numerator, denominator, scale - storedScale);
        if (significand >= BigInteger.One << 64)
        {
            significand >>= 1;
            exponent++;
            if (exponent > 16383)
                return Infinity(negative);
        }
        int storedExponent = exponent < -16382 && significand < BigInteger.One << 63 ? 0 : Math.Max(exponent, -16382) + 16383;
        if (significand.IsZero)
            storedExponent = 0;
        return new Float80(unchecked((long)(ulong)significand), storedExponent | (negative ? 0x8000 : 0));
    }

    public static Float80 FromDouble(double value)
    {
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        bool negative = (bits >> 63) != 0;
        int exponent = (int)(bits >> 52) & 2047;
        ulong fraction = bits & 0x000fffffffffffffUL;
        if (exponent == 2047)
            return fraction == 0 ? Infinity(negative) : Nan;
        if (exponent != 0)
            fraction |= 1UL << 52;
        return Pack(negative ? -(BigInteger)fraction : fraction, BigInteger.One, (exponent == 0 ? 1 : exponent) - 1023 - 52, negative);
    }

    public static Float80 FromSingle(float value) => FromDouble(value);
    public static Float80 FromSigned(long value) => Pack(value, BigInteger.One, 0);
    public static Float80 FromUnsigned(long value) => Pack((ulong)value, BigInteger.One, 0);
    public static Float80 FromWideUnsigned(UInt128 value) => Pack((BigInteger)value, BigInteger.One, 0);
    public static Float80 FromWideSigned(UInt128 value) => Pack((BigInteger)unchecked((Int128)value), BigInteger.One, 0);
    public static UInt128 ToWideSigned(Float80 value)
    {
        BigInteger integer = value.Scale >= 0 ? (BigInteger)value.Significand << value.Scale : (BigInteger)value.Significand >> -value.Scale;
        return unchecked((UInt128)checked((Int128)(value.Negative ? -integer : integer)));
    }
    public static UInt128 ToWideUnsigned(Float80 value)
    {
        BigInteger integer = value.Scale >= 0 ? (BigInteger)value.Significand << value.Scale : (BigInteger)value.Significand >> -value.Scale;
        return checked((UInt128)(value.Negative ? -integer : integer));
    }

    private ulong IeeeBits(int precision, int minimum, int maximum, int exponentWidth)
    {
        int fractionWidth = precision - 1;
        ulong sign = Negative ? 1UL << (fractionWidth + exponentWidth) : 0;
        ulong infinite = ((1UL << exponentWidth) - 1) << fractionWidth;
        if (IsNan)
            return sign | infinite | (1UL << (fractionWidth - 1));
        if (IsInfinity)
            return sign | infinite;
        if (IsZero)
            return sign;
        int exponent = 63 - BitOperations.LeadingZeroCount(Significand) + Scale;
        if (exponent > maximum)
            return sign | infinite;
        BigInteger significand = RoundedRatio(Significand, BigInteger.One, Scale - (Math.Max(exponent, minimum) - fractionWidth));
        if (significand >= BigInteger.One << precision)
        {
            significand >>= 1;
            exponent++;
            if (exponent > maximum)
                return sign | infinite;
        }
        int storedExponent = exponent < minimum && significand < BigInteger.One << fractionWidth ? 0 : Math.Max(exponent, minimum) + maximum;
        if (significand.IsZero)
            storedExponent = 0;
        return sign | ((ulong)storedExponent << fractionWidth) | ((ulong)significand & ((1UL << fractionWidth) - 1));
    }

    public static double ToDouble(Float80 value) => BitConverter.Int64BitsToDouble(unchecked((long)value.IeeeBits(53, -1022, 1023, 11)));
    public static float ToSingle(Float80 value) => BitConverter.Int32BitsToSingle(unchecked((int)value.IeeeBits(24, -126, 127, 8)));
    public static long ToSigned(Float80 value)
    {
        if (value.IsNan || value.IsInfinity)
            throw new ArithmeticException("Undefined out-of-range floating to integer conversion.");
        BigInteger integer = value.Scale >= 0 ? (BigInteger)value.Significand << value.Scale : (BigInteger)value.Significand >> -value.Scale;
        return checked((long)(value.Negative ? -integer : integer));
    }
    public static long ToUnsigned(Float80 value)
    {
        if (value.IsNan || value.IsInfinity)
            throw new ArithmeticException("Undefined out-of-range floating to integer conversion.");
        BigInteger integer = value.Scale >= 0 ? (BigInteger)value.Significand << value.Scale : (BigInteger)value.Significand >> -value.Scale;
        return unchecked((long)checked((ulong)(value.Negative ? -integer : integer)));
    }

    public static Float80 Negate(Float80 value) => new(unchecked((long)value.Significand), value.SignExponent ^ 0x8000);
    public static Float80 Abs(Float80 value) => new(unchecked((long)value.Significand), value.SignExponent & 0x7fff);
    public static Float80 Add(Float80 left, Float80 right)
    {
        if (left.IsNan || right.IsNan || left.IsInfinity && right.IsInfinity && left.Negative != right.Negative)
            return Nan;
        if (left.IsInfinity) return left;
        if (right.IsInfinity) return right;
        int scale = Math.Min(left.Scale, right.Scale);
        BigInteger sum = (left.Signed << (left.Scale - scale)) + (right.Signed << (right.Scale - scale));
        return Pack(sum, BigInteger.One, scale, left.Negative && right.Negative);
    }
    public static Float80 Subtract(Float80 left, Float80 right) => Add(left, Negate(right));
    public static Float80 Multiply(Float80 left, Float80 right)
    {
        bool negative = left.Negative != right.Negative;
        if (left.IsNan || right.IsNan || left.IsInfinity && right.IsZero || right.IsInfinity && left.IsZero)
            return Nan;
        if (left.IsInfinity || right.IsInfinity)
            return Infinity(negative);
        return Pack(left.Signed * right.Signed, BigInteger.One, left.Scale + right.Scale, negative);
    }
    public static Float80 Divide(Float80 left, Float80 right)
    {
        bool negative = left.Negative != right.Negative;
        if (left.IsNan || right.IsNan || left.IsInfinity && right.IsInfinity || left.IsZero && right.IsZero)
            return Nan;
        if (left.IsInfinity || right.IsZero)
            return Infinity(negative);
        if (right.IsInfinity)
            return Zero(negative);
        return Pack(negative ? -(BigInteger)left.Significand : left.Significand, right.Significand, left.Scale - right.Scale, negative);
    }
    public static Float80 Remainder(Float80 left, Float80 right)
    {
        if (left.IsNan || right.IsNan || left.IsInfinity || right.IsZero)
            return Nan;
        if (right.IsInfinity)
            return left;
        int scale = Math.Min(left.Scale, right.Scale);
        BigInteger numerator = left.Signed << (left.Scale - scale);
        BigInteger divisor = right.Signed << (right.Scale - scale);
        return Pack(numerator % divisor, BigInteger.One, scale, left.Negative);
    }
    public static Float80 FusedMultiplyAdd(Float80 left, Float80 right, Float80 addend)
    {
        if (left.IsInfinity || left.IsNan || right.IsInfinity || right.IsNan || addend.IsInfinity || addend.IsNan)
            return Add(Multiply(left, right), addend);
        int productScale = left.Scale + right.Scale;
        int scale = Math.Min(productScale, addend.Scale);
        BigInteger sum = ((left.Signed * right.Signed) << (productScale - scale)) + (addend.Signed << (addend.Scale - scale));
        return Pack(sum, BigInteger.One, scale, left.Negative != right.Negative && addend.Negative);
    }

    public static int Compare(Float80 left, Float80 right, int predicate)
    {
        bool unordered = left.IsNan || right.IsNan;
        int order = 0;
        if (!unordered)
        {
            if (left.IsInfinity || right.IsInfinity)
                order = left.IsInfinity && right.IsInfinity ? left.Negative == right.Negative ? 0 : left.Negative ? -1 : 1 : left.IsInfinity ? left.Negative ? -1 : 1 : right.Negative ? 1 : -1;
            else
            {
                int scale = Math.Min(left.Scale, right.Scale);
                order = (left.Signed << (left.Scale - scale)).CompareTo(right.Signed << (right.Scale - scale));
            }
        }
        bool result = predicate switch
        {
            0 => false, 1 => !unordered && order == 0, 2 => !unordered && order > 0,
            3 => !unordered && order >= 0, 4 => !unordered && order < 0, 5 => !unordered && order <= 0,
            6 => !unordered && order != 0, 7 => !unordered, 8 => unordered,
            9 => unordered || order == 0, 10 => unordered || order > 0, 11 => unordered || order >= 0,
            12 => unordered || order < 0, 13 => unordered || order <= 0, 14 => unordered || order != 0,
            15 => true, _ => throw new ArgumentOutOfRangeException(nameof(predicate))
        };
        return result ? 1 : 0;
    }
}