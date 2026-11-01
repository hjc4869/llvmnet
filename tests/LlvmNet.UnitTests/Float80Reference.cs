using System.Numerics;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>
/// The BigInteger implementation of <see cref="Float80"/> before PF-14, kept as the oracle for the integer one: every
/// finite encoding is the exact number significand times two to the scale, and each result is that exact value
/// rounded once (to nearest, ties to even). Two deliberate differences from the old code, which PF-14 fixed in Float80
/// too: a zero significand with a nonzero exponent (a pseudo-zero) counts as a zero in the special cases of
/// multiplication, division, remainder and the conversions to double and float, where the old code threw
/// DivideByZeroException or returned infinity; and ToWideSigned/ToWideUnsigned are only defined here for values that
/// fit (the old code threw OverflowException for the others).
/// </summary>
internal static class Float80Reference
{
    private static bool Negative(Float80 value) => (value.SignExponent & 0x8000) != 0;
    private static int Exponent(Float80 value) => value.SignExponent & 0x7fff;
    private static int Scale(Float80 value) => (Exponent(value) == 0 ? 1 : Exponent(value)) - 16383 - 63;
    private static bool IsNan(Float80 value) => Exponent(value) == 0x7fff && (value.Significand & 0x7fffffffffffffff) != 0;
    private static bool IsInfinity(Float80 value) => Exponent(value) == 0x7fff && !IsNan(value);
    private static bool IsZero(Float80 value) => value.Significand == 0 && Exponent(value) != 0x7fff;
    private static BigInteger Signed(Float80 value) => Negative(value) ? -(BigInteger)value.Significand : value.Significand;
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

    public static long RoundToInteger(Float80 value, int awayFromZero, int width)
    {
        if (IsNan(value) || IsInfinity(value)) return width == 32 ? int.MinValue : long.MinValue;
        BigInteger magnitude = value.Significand;
        int scale = Scale(value);
        BigInteger result;
        if (scale >= 0) result = magnitude << scale;
        else
        {
            BigInteger quotient = magnitude >> -scale, remainder = magnitude - (quotient << -scale), half = BigInteger.One << (-scale - 1);
            result = remainder > half || remainder == half && (awayFromZero != 0 || !quotient.IsEven) ? quotient + 1 : quotient;
        }
        if (Negative(value)) result = -result;
        BigInteger minimum = width == 32 ? int.MinValue : long.MinValue, maximum = width == 32 ? int.MaxValue : long.MaxValue;
        return result < minimum || result > maximum ? (long)minimum : (long)result;
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

    public static Float80 FromSigned(long value) => Pack(value, BigInteger.One, 0);
    public static Float80 FromUnsigned(long value) => Pack((ulong)value, BigInteger.One, 0);
    public static Float80 FromWideUnsigned(UInt128 value) => Pack((BigInteger)value, BigInteger.One, 0);
    public static Float80 FromWideSigned(UInt128 value) => Pack((BigInteger)unchecked((Int128)value), BigInteger.One, 0);

    /// <summary>The integer part, truncated toward zero, with the sign of the value (zero for NaN and infinity, which callers exclude).</summary>
    public static BigInteger Truncated(Float80 value)
    {
        if (IsNan(value) || IsInfinity(value)) return 0;
        BigInteger integer = Scale(value) >= 0 ? (BigInteger)value.Significand << Scale(value) : (BigInteger)value.Significand >> -Scale(value);
        return Negative(value) ? -integer : integer;
    }

    private static ulong IeeeBits(Float80 value, int precision, int minimum, int maximum, int exponentWidth)
    {
        int fractionWidth = precision - 1;
        ulong sign = Negative(value) ? 1UL << (fractionWidth + exponentWidth) : 0;
        ulong infinite = ((1UL << exponentWidth) - 1) << fractionWidth;
        if (IsNan(value))
            return sign | infinite | (1UL << (fractionWidth - 1));
        if (IsInfinity(value))
            return sign | infinite;
        if (IsZero(value))
            return sign;
        int exponent = 63 - BitOperations.LeadingZeroCount(value.Significand) + Scale(value);
        if (exponent > maximum)
            return sign | infinite;
        BigInteger significand = RoundedRatio(value.Significand, BigInteger.One, Scale(value) - (Math.Max(exponent, minimum) - fractionWidth));
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

    public static double ToDouble(Float80 value) => BitConverter.Int64BitsToDouble(unchecked((long)IeeeBits(value, 53, -1022, 1023, 11)));
    public static float ToSingle(Float80 value) => BitConverter.Int32BitsToSingle(unchecked((int)IeeeBits(value, 24, -126, 127, 8)));

    public static long ToSigned(Float80 value)
    {
        if (IsNan(value) || IsInfinity(value)) return long.MinValue;
        BigInteger integer = Truncated(value);
        return integer >= long.MinValue && integer <= long.MaxValue ? (long)integer : long.MinValue;
    }

    public static long ToUnsigned(Float80 value)
    {
        if (IsNan(value) || IsInfinity(value)) return long.MinValue;
        BigInteger integer = Truncated(value);
        return integer >= 0 && integer <= ulong.MaxValue ? unchecked((long)(ulong)integer) : long.MinValue;
    }

    public static Float80 Floor(Float80 value)
    {
        if (IsNan(value)) return Nan;
        if (IsInfinity(value) || value.Significand == 0 && Exponent(value) == 0 || Scale(value) >= 0) return value;
        return Pack(Signed(value) >> -Scale(value), BigInteger.One, 0, Negative(value));
    }

    public static Float80 Add(Float80 left, Float80 right)
    {
        if (IsNan(left) || IsNan(right) || IsInfinity(left) && IsInfinity(right) && Negative(left) != Negative(right))
            return Nan;
        if (IsInfinity(left)) return left;
        if (IsInfinity(right)) return right;
        int scale = Math.Min(Scale(left), Scale(right));
        BigInteger sum = (Signed(left) << (Scale(left) - scale)) + (Signed(right) << (Scale(right) - scale));
        return Pack(sum, BigInteger.One, scale, Negative(left) && Negative(right));
    }

    public static Float80 Multiply(Float80 left, Float80 right)
    {
        bool negative = Negative(left) != Negative(right);
        if (IsNan(left) || IsNan(right) || IsInfinity(left) && IsZero(right) || IsInfinity(right) && IsZero(left))
            return Nan;
        if (IsInfinity(left) || IsInfinity(right))
            return Infinity(negative);
        return Pack(Signed(left) * Signed(right), BigInteger.One, Scale(left) + Scale(right), negative);
    }

    public static Float80 Divide(Float80 left, Float80 right)
    {
        bool negative = Negative(left) != Negative(right);
        if (IsNan(left) || IsNan(right) || IsInfinity(left) && IsInfinity(right) || IsZero(left) && IsZero(right))
            return Nan;
        if (IsInfinity(left) || IsZero(right))
            return Infinity(negative);
        if (IsInfinity(right))
            return Zero(negative);
        return Pack(negative ? -(BigInteger)left.Significand : left.Significand, right.Significand, Scale(left) - Scale(right), negative);
    }

    public static Float80 Remainder(Float80 left, Float80 right)
    {
        if (IsNan(left) || IsNan(right) || IsInfinity(left) || IsZero(right))
            return Nan;
        if (IsInfinity(right))
            return left;
        int scale = Math.Min(Scale(left), Scale(right));
        BigInteger numerator = Signed(left) << (Scale(left) - scale);
        BigInteger divisor = Signed(right) << (Scale(right) - scale);
        return Pack(numerator % divisor, BigInteger.One, scale, Negative(left));
    }

    public static Float80 FusedMultiplyAdd(Float80 left, Float80 right, Float80 addend)
    {
        if (IsInfinity(left) || IsNan(left) || IsInfinity(right) || IsNan(right) || IsInfinity(addend) || IsNan(addend))
            return Add(Multiply(left, right), addend);
        int productScale = Scale(left) + Scale(right);
        int scale = Math.Min(productScale, Scale(addend));
        BigInteger sum = ((Signed(left) * Signed(right)) << (productScale - scale)) + (Signed(addend) << (Scale(addend) - scale));
        return Pack(sum, BigInteger.One, scale, Negative(left) != Negative(right) && Negative(addend));
    }
}
