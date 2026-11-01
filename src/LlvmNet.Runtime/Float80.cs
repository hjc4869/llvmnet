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
    // A zero significand is a zero whatever the exponent (below the maximum): the exact value is 0.
    private bool IsZero => Significand == 0 && Exponent != 0x7fff;
    private static Float80 Nan => new(unchecked((long)0xc000000000000000UL), 0x7fff);
    private static Float80 Infinity(bool negative) => new(long.MinValue, (negative ? 0x8000 : 0) | 0x7fff);
    private static Float80 Zero(bool negative) => new(0, negative ? 0x8000 : 0);

    /// <summary>
    /// The value nearest to (magnitude + f) times two to the scale, ties to even, where f is 0 without sticky and lies
    /// strictly between 0 and 1 with it. The magnitude is not 0, and with sticky it has more than 64 significant bits,
    /// so that f lies below the rounding position. Above the exponent range the result is infinity; below it, the last
    /// place stays at two to -16445 (denormals) and a result that rounds to nothing is a zero of the same sign.
    /// </summary>
    private static Float80 Round(bool negative, UInt128 magnitude, int scale, bool sticky = false)
    {
        int sign = negative ? 0x8000 : 0;
        int leading = (int)UInt128.LeadingZeroCount(magnitude);
        int exponent = 127 - leading + scale;
        if (exponent > 16383)
            return Infinity(negative);
        ulong significand;
        if (exponent >= -16382)
        {
            magnitude <<= leading;
            significand = (ulong)(magnitude >> 64);
            ulong rest = (ulong)magnitude;
            if (rest > 1UL << 63 || rest == 1UL << 63 && (sticky || (significand & 1) != 0))
            {
                if (++significand == 0)
                {
                    significand = 1UL << 63;
                    if (++exponent > 16383)
                        return Infinity(negative);
                }
            }
            return new Float80(unchecked((long)significand), (exponent + 16383) | sign);
        }
        int shift = -16445 - scale;
        if (shift <= 0)
            significand = (ulong)(magnitude << -shift);
        else if (shift < 128)
        {
            UInt128 half = UInt128.One << (shift - 1), rest = magnitude & ((half << 1) - 1);
            significand = (ulong)(magnitude >> shift);
            if (rest > half || rest == half && (sticky || (significand & 1) != 0))
                significand++;
        }
        else
            significand = shift == 128 && (magnitude > UInt128.One << 127 || magnitude == UInt128.One << 127 && sticky) ? 1UL : 0;
        // Rounding up to two to 63 reaches the smallest normal number.
        return new Float80(unchecked((long)significand), (int)(significand >> 63) | sign);
    }

    // A nonzero magnitude times two to the scale that is exactly a normal number (integers of 64 bits, doubles).
    private static Float80 Normal(bool negative, ulong magnitude, int scale)
    {
        int leading = BitOperations.LeadingZeroCount(magnitude);
        return new Float80(unchecked((long)(magnitude << leading)), (scale - leading + 63 + 16383) | (negative ? 0x8000 : 0));
    }

    // value / 2^shift rounded to the nearest integer, ties to even or away from zero; a negative shift is exact.
    private static ulong RoundRight(ulong value, int shift, bool awayFromZero = false)
    {
        if (shift <= 0)
            return value << -shift;
        if (shift > 64)
            return 0;
        ulong quotient = shift == 64 ? 0 : value >> shift, rest = shift == 64 ? value : value & ((1UL << shift) - 1), half = 1UL << (shift - 1);
        return rest > half || rest == half && (awayFromZero || (quotient & 1) != 0) ? quotient + 1 : quotient;
    }

    // The magnitude of a finite value truncated toward zero; false when it needs more than 128 bits.
    private static bool Truncate(Float80 value, out UInt128 magnitude)
    {
        int scale = value.Scale;
        ulong significand = value.Significand;
        if (scale <= 0)
        {
            magnitude = scale <= -64 ? 0 : significand >> -scale;
            return true;
        }
        magnitude = significand == 0 ? 0 : (UInt128)significand << Math.Min(scale, 127);
        return significand == 0 || scale <= 64 + BitOperations.LeadingZeroCount(significand);
    }

    /// <summary>
    /// (high * 2^64 + low) / divisor for a divisor with the top bit set and high below it: Knuth's algorithm D with
    /// 32-bit digits (divlu in Hacker's Delight), two 64-bit divisions and at most two corrections each.
    /// </summary>
    internal static ulong DivideWide(ulong high, ulong low, ulong divisor, out ulong remainder)
    {
        const ulong Digit = 1UL << 32;
        ulong divisorHigh = divisor >> 32, divisorLow = (uint)divisor, lowHigh = low >> 32, lowLow = (uint)low;
        ulong upper = high / divisorHigh, estimate = high - upper * divisorHigh;
        while (upper >= Digit || upper * divisorLow > (estimate << 32 | lowHigh))
        {
            upper--;
            estimate += divisorHigh;
            if (estimate >= Digit)
                break;
        }
        ulong middle = unchecked((high << 32 | lowHigh) - upper * divisor);
        ulong lower = middle / divisorHigh;
        estimate = middle - lower * divisorHigh;
        while (lower >= Digit || lower * divisorLow > (estimate << 32 | lowLow))
        {
            lower--;
            estimate += divisorHigh;
            if (estimate >= Digit)
                break;
        }
        remainder = unchecked((middle << 32 | lowLow) - lower * divisor);
        return upper << 32 | lower;
    }

    /// <summary>Loads exactly the 10-byte store size of x86_fp80; the 6 padding bytes are not read.</summary>
    public static unsafe Float80 Read(nint address) =>
        new(unchecked((long)System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>((void*)address)),
            System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ushort>((void*)(address + 8)));

    /// <summary>Stores exactly 10 bytes, as LLVM's store of x86_fp80 does; neighbouring bytes are untouched.</summary>
    public static unsafe void Write(nint address, Float80 value)
    {
        System.Runtime.CompilerServices.Unsafe.WriteUnaligned((void*)address, value.Significand);
        System.Runtime.CompilerServices.Unsafe.WriteUnaligned((void*)(address + 8), value.SignExponent);
    }

    /// <summary>llvm.is.fpclass: snan 1, qnan 2, -inf 4, -normal 8, -subnormal 16, -0 32, +0 64, +subnormal 128, +normal 256, +inf 512.</summary>
    public static int Class(Float80 value, int mask)
    {
        int bit;
        if (value.IsNan) bit = (value.Significand & 0x4000000000000000) != 0 ? 2 : 1;
        else if (value.IsInfinity) bit = value.Negative ? 4 : 512;
        else if (value.Significand == 0) bit = value.Negative ? 32 : 64;
        else if (value.Exponent == 0) bit = value.Negative ? 16 : 128;
        else bit = value.Negative ? 8 : 256;
        return (mask & bit) != 0 ? 1 : 0;
    }

    /// <summary>
    /// llvm.lround/llround (ties away from zero) and llvm.lrint/llrint (ties to even, the default rounding mode),
    /// computed exactly. NaN, infinity and out-of-range values give the x86 "integer indefinite" value instead of trapping.
    /// </summary>
    public static long RoundToInteger(Float80 value, int awayFromZero, int width)
    {
        long minimum = width == 32 ? int.MinValue : long.MinValue, maximum = width == 32 ? int.MaxValue : long.MaxValue;
        if (value.IsNan || value.IsInfinity) return minimum;
        ulong significand = value.Significand, magnitude;
        int scale = value.Scale;
        if (scale < 0) magnitude = RoundRight(significand, -scale, awayFromZero != 0);
        else if (significand == 0) magnitude = 0;
        else if (scale <= BitOperations.LeadingZeroCount(significand)) magnitude = significand << scale;
        else return minimum;
        if (value.Negative) return magnitude <= (ulong)maximum + 1 ? unchecked((long)(0 - magnitude)) : minimum;
        return magnitude <= (ulong)maximum ? (long)magnitude : minimum;
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
        return fraction == 0 ? Zero(negative) : Normal(negative, fraction, (exponent == 0 ? 1 : exponent) - 1023 - 52);
    }

    public static Float80 FromSingle(float value) => FromDouble(value);
    public static Float80 FromSigned(long value) => value == 0 ? Zero(false) : Normal(value < 0, value < 0 ? unchecked(0 - (ulong)value) : (ulong)value, 0);
    public static Float80 FromUnsigned(long value) => value == 0 ? Zero(false) : Normal(false, (ulong)value, 0);
    public static Float80 FromWideUnsigned(UInt128 value) => value == 0 ? Zero(false) : Round(false, value, 0);
    public static Float80 FromWideSigned(UInt128 value)
    {
        bool negative = (Int128)value < 0;
        return value == 0 ? Zero(false) : Round(negative, negative ? 0 - value : value, 0);
    }

    // LLVM leaves out-of-range conversions to i128 poison. Native code calls compiler-rt's __fixxfti and __fixunsxfti,
    // and these results follow them: magnitudes of 2^128 and more and infinities saturate, NaN gives the minimum,
    // negative values give 0 when unsigned, and a NaN with the sign clear gives the maximum. Just above the range,
    // where compiler-rt wraps around, values saturate too.
    public static UInt128 ToWideSigned(Float80 value)
    {
        if (value.IsNan) return unchecked((UInt128)Int128.MinValue);
        bool fits = Truncate(value, out UInt128 magnitude) && !value.IsInfinity;
        if (value.Negative) return fits && magnitude <= UInt128.One << 127 ? 0 - magnitude : unchecked((UInt128)Int128.MinValue);
        return fits && magnitude < UInt128.One << 127 ? magnitude : (UInt128)Int128.MaxValue;
    }
    public static UInt128 ToWideUnsigned(Float80 value)
    {
        if (value.Negative) return 0;
        return !value.IsNan && !value.IsInfinity && Truncate(value, out UInt128 magnitude) ? magnitude : UInt128.MaxValue;
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
        ulong significand = RoundRight(Significand, Math.Max(exponent, minimum) - fractionWidth - Scale);
        if (significand >> precision != 0)
        {
            significand >>= 1;
            exponent++;
            if (exponent > maximum)
                return sign | infinite;
        }
        int storedExponent = exponent < minimum && significand < 1UL << fractionWidth ? 0 : Math.Max(exponent, minimum) + maximum;
        if (significand == 0)
            storedExponent = 0;
        return sign | ((ulong)storedExponent << fractionWidth) | (significand & ((1UL << fractionWidth) - 1));
    }

    public static double ToDouble(Float80 value) => BitConverter.Int64BitsToDouble(unchecked((long)value.IeeeBits(53, -1022, 1023, 11)));
    public static float ToSingle(Float80 value) => BitConverter.Int32BitsToSingle(unchecked((int)value.IeeeBits(24, -126, 127, 8)));
    // Out-of-range conversions are poison in LLVM; like the x87 instructions native code uses, they produce the
    // "integer indefinite" value 0x8000000000000000.
    private const long IntegerIndefinite = long.MinValue;

    public static long ToSigned(Float80 value)
    {
        if (value.IsNan || value.IsInfinity || !Truncate(value, out UInt128 magnitude)) return IntegerIndefinite;
        if (value.Negative) return magnitude <= 1UL << 63 ? unchecked((long)(0 - (ulong)magnitude)) : IntegerIndefinite;
        return magnitude <= long.MaxValue ? (long)magnitude : IntegerIndefinite;
    }
    public static long ToUnsigned(Float80 value)
    {
        if (value.IsNan || value.IsInfinity || !Truncate(value, out UInt128 magnitude)) return IntegerIndefinite;
        if (value.Negative) return magnitude == 0 ? 0 : IntegerIndefinite;
        return magnitude <= ulong.MaxValue ? unchecked((long)(ulong)magnitude) : IntegerIndefinite;
    }

    public static Float80 Negate(Float80 value) => new(unchecked((long)value.Significand), value.SignExponent ^ 0x8000);
    public static Float80 Abs(Float80 value) => new(unchecked((long)value.Significand), value.SignExponent & 0x7fff);
    public static Float80 CopySign(Float80 magnitude, Float80 sign) =>
        new(unchecked((long)magnitude.Significand), magnitude.SignExponent & 0x7fff | sign.SignExponent & 0x8000);
    public static Float80 Floor(Float80 value)
    {
        if (value.IsNan) return Nan;
        int scale = value.Scale;
        if (value.IsInfinity || scale >= 0) return value;
        ulong significand = value.Significand, integer = scale <= -64 ? 0 : significand >> -scale;
        bool fraction = scale <= -64 ? significand != 0 : (significand & ((1UL << -scale) - 1)) != 0;
        if (value.Negative && fraction) integer++;
        return integer == 0 ? Zero(value.Negative) : Normal(value.Negative, integer, 0);
    }
    public static Float80 Add(Float80 left, Float80 right)
    {
        if (left.IsNan || right.IsNan || left.IsInfinity && right.IsInfinity && left.Negative != right.Negative)
            return Nan;
        if (left.IsInfinity) return left;
        if (right.IsInfinity) return right;
        ulong a = left.Significand, b = right.Significand;
        if (a == 0 || b == 0)
            return a != 0 ? Round(left.Negative, a, left.Scale) : b != 0 ? Round(right.Negative, b, right.Scale) : Zero(left.Negative && right.Negative);
        int leftShift = BitOperations.LeadingZeroCount(a), rightShift = BitOperations.LeadingZeroCount(b);
        a <<= leftShift;
        b <<= rightShift;
        int leftExponent = left.Scale - leftShift, rightExponent = right.Scale - rightShift;
        bool negative = left.Negative;
        if (leftExponent < rightExponent || leftExponent == rightExponent && a < b)
        {
            (a, b, leftExponent, rightExponent, negative) = (b, a, rightExponent, leftExponent, right.Negative);
        }
        // The larger magnitude goes to bits 126 to 63, the smaller one below it. Bits shifted out past bit 0 leave
        // only a sticky bit; then the operands are more than 63 bits apart and the sum keeps over 120 bits.
        int distance = leftExponent - rightExponent;
        UInt128 larger = (UInt128)a << 63, smaller;
        bool sticky = false;
        if (distance <= 63)
            smaller = (UInt128)b << (63 - distance);
        else if (distance < 127)
        {
            smaller = b >> (distance - 63);
            sticky = b << (127 - distance) != 0;
        }
        else
        {
            smaller = 0;
            sticky = true;
        }
        UInt128 sum;
        if (left.Negative == right.Negative)
            sum = larger + smaller;
        else
        {
            // larger - (smaller + f) with 0 < f < 1 is (larger - smaller - 1) + (1 - f).
            sum = larger - smaller - (sticky ? 1U : 0U);
            if (sum == 0)
                return Zero(false);
        }
        return Round(negative, sum, leftExponent - 63, sticky);
    }
    public static Float80 Subtract(Float80 left, Float80 right) => Add(left, Negate(right));
    public static Float80 Multiply(Float80 left, Float80 right)
    {
        bool negative = left.Negative != right.Negative;
        if (left.IsNan || right.IsNan || left.IsInfinity && right.IsZero || right.IsInfinity && left.IsZero)
            return Nan;
        if (left.IsInfinity || right.IsInfinity)
            return Infinity(negative);
        ulong high = Math.BigMul(left.Significand, right.Significand, out ulong low);
        return (high | low) == 0 ? Zero(negative) : Round(negative, new UInt128(high, low), left.Scale + right.Scale);
    }
    public static Float80 Divide(Float80 left, Float80 right)
    {
        bool negative = left.Negative != right.Negative;
        if (left.IsNan || right.IsNan || left.IsInfinity && right.IsInfinity || left.IsZero && right.IsZero)
            return Nan;
        if (left.IsInfinity || right.IsZero)
            return Infinity(negative);
        if (right.IsInfinity || left.IsZero)
            return Zero(negative);
        ulong a = left.Significand, b = right.Significand;
        int leftShift = BitOperations.LeadingZeroCount(a), rightShift = BitOperations.LeadingZeroCount(b);
        a <<= leftShift;
        b <<= rightShift;
        int scale = left.Scale - leftShift - (right.Scale - rightShift);
        // With both normalized a / b lies between 1/2 and 2: a * 2^64 / b has 65 bits, or 64 and one more is
        // taken, so a rounding bit is always there. The remainder gives the sticky bit; after an extra bit of 1 the
        // remainder left is never 0 (b times an odd number cannot equal a * 2^65 for b below 2^64).
        UInt128 quotient;
        ulong remainder;
        if (a >= b)
        {
            quotient = new UInt128(1, DivideWide(a - b, 0, b, out remainder));
            scale -= 64;
        }
        else
        {
            quotient = (UInt128)DivideWide(a, 0, b, out remainder) << 1 | (remainder >= b - remainder ? 1U : 0U);
            scale -= 65;
        }
        return Round(negative, quotient, scale, remainder != 0);
    }
    public static Float80 Remainder(Float80 left, Float80 right)
    {
        if (left.IsNan || right.IsNan || left.IsInfinity || right.IsZero)
            return Nan;
        if (right.IsInfinity)
            return left;
        if (left.IsZero)
            return Zero(left.Negative);
        ulong a = left.Significand, b = right.Significand;
        int leftShift = BitOperations.LeadingZeroCount(a), rightShift = BitOperations.LeadingZeroCount(b);
        a <<= leftShift;
        b <<= rightShift;
        int leftExponent = left.Scale - leftShift, rightExponent = right.Scale - rightShift;
        if (leftExponent < rightExponent)
            return Round(left.Negative, a, leftExponent);
        // (a * 2^distance) mod b, taking up to 64 bits of the distance per division.
        ulong remainder = a >= b ? a - b : a;
        for (int distance = leftExponent - rightExponent; distance > 0 && remainder != 0;)
        {
            int step = Math.Min(distance, 64);
            UInt128 shifted = (UInt128)remainder << step;
            DivideWide((ulong)(shifted >> 64), (ulong)shifted, b, out remainder);
            distance -= step;
        }
        return remainder == 0 ? Zero(left.Negative) : Round(left.Negative, remainder, rightExponent);
    }
    public static Float80 MultiplyAdd(Float80 left, Float80 right, Float80 addend) => Add(Multiply(left, right), addend);
    public static Float80 FusedMultiplyAdd(Float80 left, Float80 right, Float80 addend)
    {
        if (left.IsInfinity || left.IsNan || right.IsInfinity || right.IsNan || addend.IsInfinity || addend.IsNan)
            return Add(Multiply(left, right), addend);
        bool productNegative = left.Negative != right.Negative;
        ulong productHigh = Math.BigMul(left.Significand, right.Significand, out ulong productLow);
        UInt128 product = new(productHigh, productLow);
        int productScale = left.Scale + right.Scale;
        if (product == 0)
            return addend.Significand == 0 ? Zero(productNegative && addend.Negative) : Round(addend.Negative, addend.Significand, addend.Scale);
        if (addend.Significand == 0)
            return Round(productNegative, product, productScale);
        // The exact sum in 256 bits (high, low): the operand with the higher leading bit has it at bit 254 and the
        // other one is aligned below. Bits shifted out past bit 0 leave only a sticky bit; then the operands are more
        // than 127 bits apart and the sum keeps over 120 bits.
        UInt128 larger = product, smaller = addend.Significand;
        int largerTop = 127 - (int)UInt128.LeadingZeroCount(product), largerScale = productScale, smallerScale = addend.Scale;
        bool negative = productNegative, subtract = productNegative != addend.Negative;
        int addendTop = 63 - BitOperations.LeadingZeroCount(addend.Significand);
        if (addend.Scale + addendTop > productScale + largerTop)
            (larger, smaller, largerTop, largerScale, smallerScale, negative) = (smaller, larger, addendTop, addend.Scale, productScale, addend.Negative);
        int windowScale = largerScale + largerTop - 254, smallerShift = smallerScale - windowScale;
        ShiftLeft(larger, 254 - largerTop, out UInt128 high, out UInt128 low);
        UInt128 otherHigh, otherLow;
        bool sticky = false;
        if (smallerShift >= 0)
            ShiftLeft(smaller, smallerShift, out otherHigh, out otherLow);
        else
        {
            otherHigh = 0;
            otherLow = smallerShift > -128 ? smaller >> -smallerShift : 0;
            sticky = smallerShift > -128 ? (smaller & ((UInt128.One << -smallerShift) - 1)) != 0 : true;
        }
        if (!subtract)
        {
            low += otherLow;
            high += otherHigh + (low < otherLow ? 1U : 0U);
        }
        else
        {
            UInt128 borrow = low < otherLow ? 1U : 0U;
            low -= otherLow;
            high -= otherHigh + borrow;
            if (sticky)
            {
                if (low == 0)
                    high--;
                low--;
            }
            if (high >> 127 != 0)
            {
                // The smaller leading bit had the larger value (equal leading exponents, so no sticky bit).
                low = 0 - low;
                high = ~high + (low == 0 ? 1U : 0U);
                negative = !negative;
            }
            if (high == 0 && low == 0)
                return Zero(false);
        }
        int scale = windowScale;
        if (high != 0)
        {
            int shift = 128 - (int)UInt128.LeadingZeroCount(high);
            sticky |= shift == 128 ? low != 0 : low << (128 - shift) != 0;
            low = shift == 128 ? high : high << (128 - shift) | low >> shift;
            scale += shift;
        }
        return Round(negative, low, scale, sticky);
    }

    // value << shift as 256 bits, for 0 <= shift < 256.
    private static void ShiftLeft(UInt128 value, int shift, out UInt128 high, out UInt128 low)
    {
        if (shift == 0)
            (high, low) = (0, value);
        else if (shift < 128)
            (high, low) = (value >> (128 - shift), value << shift);
        else
            (high, low) = (value << (shift - 128), 0);
    }

    /// <summary>
    /// The order of two values that are not NaN, as the exact numbers significand times two to the scale: zeros of
    /// either sign are equal, then the signs decide, then the magnitudes, compared by binary exponent and significand
    /// once both are normalized. No wide integers are built (printf compares long doubles for every digit it writes).
    /// </summary>
    private static int Order(Float80 left, Float80 right)
    {
        if (left.IsInfinity || right.IsInfinity)
            return left.IsInfinity && right.IsInfinity ? left.Negative == right.Negative ? 0 : left.Negative ? -1 : 1 : left.IsInfinity ? left.Negative ? -1 : 1 : right.Negative ? 1 : -1;
        if (left.Significand == 0 || right.Significand == 0)
            return left.Significand == right.Significand ? 0 : left.Significand == 0 ? right.Negative ? 1 : -1 : left.Negative ? -1 : 1;
        if (left.Negative != right.Negative)
            return left.Negative ? -1 : 1;
        int leftShift = BitOperations.LeadingZeroCount(left.Significand), rightShift = BitOperations.LeadingZeroCount(right.Significand);
        int leftExponent = left.Scale - leftShift, rightExponent = right.Scale - rightShift;
        int magnitude = leftExponent != rightExponent ? leftExponent.CompareTo(rightExponent) : (left.Significand << leftShift).CompareTo(right.Significand << rightShift);
        return left.Negative ? -magnitude : magnitude;
    }

    // llvm.maxnum and llvm.minnum (C fmaxl, fminl): a NaN operand yields the other one; LLVM leaves the sign of zero open.
    public static Float80 MaxNumber(Float80 left, Float80 right) => left.IsNan ? right : right.IsNan ? left : Compare(left, right, 3) != 0 ? left : right;
    public static Float80 MinNumber(Float80 left, Float80 right) => left.IsNan ? right : right.IsNan ? left : Compare(left, right, 5) != 0 ? left : right;

    public static int Compare(Float80 left, Float80 right, int predicate)
    {
        bool unordered = left.IsNan || right.IsNan;
        int order = unordered ? 0 : Order(left, right);
        bool result = predicate switch
        {
            0 => false,
            1 => !unordered && order == 0,
            2 => !unordered && order > 0,
            3 => !unordered && order >= 0,
            4 => !unordered && order < 0,
            5 => !unordered && order <= 0,
            6 => !unordered && order != 0,
            7 => !unordered,
            8 => unordered,
            9 => unordered || order == 0,
            10 => unordered || order > 0,
            11 => unordered || order >= 0,
            12 => unordered || order < 0,
            13 => unordered || order <= 0,
            14 => unordered || order != 0,
            15 => true,
            _ => throw new ArgumentOutOfRangeException(nameof(predicate))
        };
        return result ? 1 : 0;
    }
}
