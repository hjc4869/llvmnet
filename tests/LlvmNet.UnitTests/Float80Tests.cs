using System.Numerics;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>Known x87 extended-precision encodings and exactly representable results.</summary>
public sealed class Float80Tests
{
    private static Float80 Value(double value) => Float80.FromDouble(value);
    private static void Encoded(Float80 value, ulong significand, int signExponent)
    {
        Assert.Equal(significand.ToString("x16"), value.Significand.ToString("x16"));
        Assert.Equal(signExponent.ToString("x4"), value.SignExponent.ToString("x4"));
    }

    [Fact] public void One() => Encoded(Value(1.0), 0x8000000000000000, 0x3fff);
    [Fact] public void NegativeTwo() => Encoded(Value(-2.0), 0x8000000000000000, 0xc000);
    [Fact] public void OneThirdRoundsToNearest() => Encoded(Float80.Divide(Value(1), Value(3)), 0xaaaaaaaaaaaaaaab, 0x3ffd);
    [Fact] public void TwoThirdsRoundsToNearest() => Encoded(Float80.Divide(Value(2), Value(3)), 0xaaaaaaaaaaaaaaab, 0x3ffe);
    [Fact] public void OneTenth() => Encoded(Float80.Divide(Value(1), Value(10)), 0xcccccccccccccccd, 0x3ffb);
    [Fact] public void SmallestDoubleIsNormalInExtendedPrecision() => Encoded(Value(double.Epsilon), 0x8000000000000000, 0x3bcd);
    [Fact] public void LargestFinite() => Encoded(Float80.Multiply(new Float80(-1, 0x7ffe), Value(1)), 0xffffffffffffffff, 0x7ffe);

    [Fact]
    public void OverflowBecomesInfinity()
    {
        Float80 product = Float80.Multiply(new Float80(-1, 0x7ffe), Value(2));
        Assert.Equal(double.PositiveInfinity, Float80.ToDouble(product));
    }

    [Fact]
    public void ExtraPrecisionIsKeptBeyondDouble()
    {
        Float80 sum = Float80.Add(Value(1), Value(Math.ScaleB(1, -63)));
        Encoded(sum, 0x8000000000000001, 0x3fff);
        Assert.Equal(1.0, Float80.ToDouble(sum));
    }

    [Fact]
    public void TiesRoundToEven()
    {
        Encoded(Float80.Add(Value(1), Value(Math.ScaleB(1, -64))), 0x8000000000000000, 0x3fff);
        Float80 odd = Float80.Add(Value(1), Value(Math.ScaleB(1, -63)));
        Encoded(Float80.Add(odd, Value(Math.ScaleB(1, -64))), 0x8000000000000002, 0x3fff);
    }

    [Fact]
    public void ExactProductOfThirtyTwoBitSignificands()
    {
        var random = new Random(1234);
        for (int index = 0; index < 1000; index++)
        {
            long left = random.NextInt64(1, 1L << 32), right = random.NextInt64(1, 1L << 32);
            Float80 product = Float80.Multiply(Float80.FromSigned(left), Float80.FromSigned(right));
            Assert.Equal((UInt128)(ulong)(left * right), Float80.ToWideUnsigned(product));
        }
    }

    [Fact]
    public void ConversionsRoundTripThroughDouble()
    {
        foreach (double value in new[] { 0.0, -0.0, 1.5, -1e300, 1e-300, Math.PI, double.MaxValue, double.Epsilon })
        {
            double result = Float80.ToDouble(Value(value));
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(result));
        }
    }

    [Fact]
    public void ComparisonsFollowLlvmPredicates()
    {
        Float80 nan = Float80.Divide(Value(0), Value(0));
        Float80 one = Value(1), two = Value(2);
        Assert.Equal(1, Float80.Compare(one, two, 4));   // olt
        Assert.Equal(0, Float80.Compare(two, one, 4));
        Assert.Equal(0, Float80.Compare(nan, nan, 1));   // oeq
        Assert.Equal(1, Float80.Compare(nan, one, 8));   // uno
        Assert.Equal(1, Float80.Compare(nan, one, 14));  // une
        Assert.Equal(1, Float80.Compare(Value(0), Value(-0.0), 1));
    }

    [Fact]
    public void FloorAndTruncatingConversions()
    {
        Assert.Equal(-1.0, Float80.ToDouble(Float80.Floor(Value(-0.5))));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(Float80.ToDouble(Float80.Floor(Value(-0.0)))));
        Assert.Equal(-2, Float80.ToSigned(Value(-2.9)));
        Assert.Equal(long.MaxValue, Float80.ToSigned(Float80.FromSigned(long.MaxValue)));
        Assert.Equal(unchecked((long)ulong.MaxValue), Float80.ToUnsigned(Float80.FromUnsigned(-1)));
    }

    [Fact]
    public void RemainderAndFusedMultiplyAdd()
    {
        Assert.Equal(1.0, Float80.ToDouble(Float80.Remainder(Value(7), Value(3))));
        Float80 fused = Float80.FusedMultiplyAdd(Float80.Add(Value(1), Value(Math.ScaleB(1, -40))),
            Float80.Subtract(Value(1), Value(Math.ScaleB(1, -40))), Value(-1));
        Assert.Equal(-Math.ScaleB(1, -80), Float80.ToDouble(fused));
    }

    /// <summary>
    /// Ordered comparisons agree with the exact values, significand times two to the scale, for zeros of both signs,
    /// denormals, unnormals (integer bit clear), normals across the exponent range and infinities.
    /// </summary>
    [Fact]
    public void ComparisonsFollowTheExactValues()
    {
        var random = new Random(80);
        ulong[] significands = [0, 1, 0x8000000000000000, 0xffffffffffffffff, 0x4000000000000000, 0xc000000000000001];
        int[] exponents = [0, 1, 2, 64, 0x3ffe, 0x3fff, 0x4000, 0x7ffd, 0x7ffe];
        var values = new List<Float80>();
        foreach (ulong significand in significands)
            foreach (int exponent in exponents)
                foreach (int sign in new[] { 0, 0x8000 })
                    values.Add(new Float80(unchecked((long)significand), sign | exponent));
        for (int index = 0; index < 200; index++)
            values.Add(new Float80(random.NextInt64(), random.Next(0x10000)));
        values.Add(new Float80(long.MinValue, 0x7fff));
        values.Add(new Float80(long.MinValue, 0xffff));
        values.RemoveAll(value => (value.SignExponent & 0x7fff) == 0x7fff && (value.Significand & 0x7fffffffffffffff) != 0);
        static int Exact(Float80 value, out BigInteger magnitude)
        {
            int exponent = value.SignExponent & 0x7fff;
            magnitude = (BigInteger)value.Significand << ((exponent == 0 ? 1 : exponent) - 1);
            return (value.SignExponent & 0x8000) != 0 ? -1 : 1;
        }
        foreach (Float80 left in values)
            foreach (Float80 right in values)
            {
                bool leftInfinite = (left.SignExponent & 0x7fff) == 0x7fff, rightInfinite = (right.SignExponent & 0x7fff) == 0x7fff;
                int expected;
                if (leftInfinite || rightInfinite)
                    expected = Math.Sign((leftInfinite ? ((left.SignExponent & 0x8000) != 0 ? -2 : 2) : 0) - (rightInfinite ? ((right.SignExponent & 0x8000) != 0 ? -2 : 2) : 0));
                else
                {
                    BigInteger a = Exact(left, out BigInteger leftMagnitude) * leftMagnitude, b = Exact(right, out BigInteger rightMagnitude) * rightMagnitude;
                    expected = a.CompareTo(b);
                }
                Assert.Equal(expected < 0 ? 1 : 0, Float80.Compare(left, right, 4));
                Assert.Equal(expected == 0 ? 1 : 0, Float80.Compare(left, right, 1));
                Assert.Equal(expected > 0 ? 1 : 0, Float80.Compare(left, right, 2));
            }
    }

    // PF-14: the integer arithmetic against the BigInteger implementation it replaced (Float80Reference), bit for bit,
    // over encodings that stress rounding: zeros, denormals, pseudo-denormals and unnormals (integer bit clear),
    // exponents at the edges of the range and of the double and float ranges, close operands that cancel, operands far
    // apart, and random ones.

    private static readonly ulong[] SpecialSignificands =
        [0, 1, 2, 3, 0x7fffffffffffffff, 0x8000000000000000, 0x8000000000000001, 0xffffffffffffffff, 0xfffffffffffffffe,
         0xc000000000000000, 0x4000000000000000, 0x8000000000000400, 0x80000000000007ff, 0x8000000000000800, 0x8000010000000000];
    private static readonly int[] SpecialExponents =
        [0, 1, 2, 3, 62, 63, 64, 65, 66, 127, 128, 0x3bcd - 64, 0x3bcc, 0x3bcd, 0x3c00, 0x3f6a, 0x3f81, 0x3ffd, 0x3ffe, 0x3fff,
         0x4000, 0x403d, 0x403e, 0x403f, 0x407d, 0x407e, 0x407f, 0x43fe, 0x43ff, 0x407f, 0x7ffc, 0x7ffd, 0x7ffe, 0x7fff];

    private static Float80 RandomEncoding(Random random)
    {
        ulong significand = random.Next(4) switch
        {
            0 => SpecialSignificands[random.Next(SpecialSignificands.Length)],
            1 => (ulong)random.NextInt64() | 0x8000000000000000,
            2 => (ulong)random.NextInt64() >> random.Next(64),
            _ => ((ulong)random.NextInt64() | 0x8000000000000000) & ~((1UL << random.Next(64)) - 1)
        };
        int exponent = random.Next(3) == 0 ? SpecialExponents[random.Next(SpecialExponents.Length)] : random.Next(0x8000);
        return new Float80(unchecked((long)significand), exponent | (random.Next(2) == 0 ? 0x8000 : 0));
    }

    // A second operand near the first one: the same or a nearby exponent and a significand that differs in a few bits.
    private static Float80 RandomPartner(Random random, Float80 first)
    {
        if (random.Next(3) == 0)
            return RandomEncoding(random);
        int exponent = Math.Clamp((first.SignExponent & 0x7fff) + random.Next(-70, 71) * (random.Next(4) == 0 ? 1 : 0) + random.Next(-2, 3), 0, 0x7fff);
        ulong significand = first.Significand;
        int flips = random.Next(4);
        for (int index = 0; index < flips; index++)
            significand ^= 1UL << random.Next(64);
        if (random.Next(4) == 0)
            significand = unchecked(significand + (ulong)random.Next(-3, 4));
        return new Float80(unchecked((long)significand), exponent | (random.Next(2) == 0 ? first.SignExponent & 0x8000 : (first.SignExponent ^ 0x8000) & 0x8000));
    }

    private static string Show(Float80 value) => $"{value.SignExponent:x4}:{value.Significand:x16}";

    private static void Same(Float80 expected, Float80 actual, string operation)
    {
        if (expected.Significand != actual.Significand || expected.SignExponent != actual.SignExponent)
            Assert.Fail($"{operation}: expected {Show(expected)}, got {Show(actual)}");
    }

    private static void CompareBinary(int seed, int count, string name, Func<Float80, Float80, Float80> actual, Func<Float80, Float80, Float80> expected)
    {
        var random = new Random(seed);
        foreach (Float80 left in SpecialValues())
            foreach (Float80 right in SpecialValues())
                Same(expected(left, right), actual(left, right), $"{name}({Show(left)}, {Show(right)})");
        for (int index = 0; index < count; index++)
        {
            Float80 left = RandomEncoding(random), right = RandomPartner(random, left);
            if (random.Next(2) == 0)
                (left, right) = (right, left);
            Same(expected(left, right), actual(left, right), $"{name}({Show(left)}, {Show(right)})");
        }
    }

    private static IEnumerable<Float80> SpecialValues()
    {
        foreach (ulong significand in new ulong[] { 0, 1, 0x8000000000000000, 0xffffffffffffffff, 0x4000000000000000, 0xc000000000000001 })
            foreach (int exponent in new[] { 0, 1, 2, 0x3ffe, 0x3fff, 0x4000, 0x7ffe, 0x7fff })
                foreach (int sign in new[] { 0, 0x8000 })
                    yield return new Float80(unchecked((long)significand), sign | exponent);
    }

    [Fact] public void AddMatchesTheReference() => CompareBinary(14, 300_000, "Add", Float80.Add, Float80Reference.Add);
    [Fact] public void SubtractMatchesTheReference() => CompareBinary(15, 100_000, "Subtract", Float80.Subtract, (a, b) => Float80Reference.Add(a, Float80.Negate(b)));
    [Fact] public void MultiplyMatchesTheReference() => CompareBinary(16, 300_000, "Multiply", Float80.Multiply, Float80Reference.Multiply);
    [Fact] public void DivideMatchesTheReference() => CompareBinary(17, 300_000, "Divide", Float80.Divide, Float80Reference.Divide);
    [Fact] public void RemainderMatchesTheReference() => CompareBinary(18, 40_000, "Remainder", Float80.Remainder, Float80Reference.Remainder);
    [Fact] public void MultiplyAddMatchesTheReference() =>
        CompareBinary(19, 50_000, "MultiplyAdd", (a, b) => Float80.MultiplyAdd(a, b, Float80.Negate(a)), (a, b) => Float80Reference.Add(Float80Reference.Multiply(a, b), Float80.Negate(a)));

    [Fact]
    public void FusedMultiplyAddMatchesTheReference()
    {
        var random = new Random(20);
        for (int index = 0; index < 60_000; index++)
        {
            Float80 left = RandomEncoding(random), right = RandomEncoding(random);
            Float80 product = Float80Reference.Multiply(left, right);
            // Mostly an addend near the rounded product, so that the exact product's low bits decide the result.
            Float80 addend = random.Next(3) == 0 ? RandomEncoding(random) : RandomPartner(random, Float80.Negate(product));
            Same(Float80Reference.FusedMultiplyAdd(left, right, addend), Float80.FusedMultiplyAdd(left, right, addend),
                $"FusedMultiplyAdd({Show(left)}, {Show(right)}, {Show(addend)})");
        }
    }

    [Fact]
    public void ConversionsMatchTheReference()
    {
        var random = new Random(21);
        var values = new List<Float80>(SpecialValues());
        for (int index = 0; index < 200_000; index++)
            values.Add(RandomEncoding(random));
        // Around the double and float rounding boundaries and the integer range limits.
        foreach (int exponent in new[] { 0x3c00, 0x3bcc, 0x3bcd, 0x3f6a, 0x3f81, 0x43fe, 0x43ff, 0x407e, 0x407f, 0x401d, 0x401e, 0x403d, 0x403e, 0x403f, 0x3ffd, 0x3ffe })
            for (int index = 0; index < 2_000; index++)
                values.Add(new Float80(unchecked((long)(0x8000000000000000 | ((ulong)random.NextInt64() & ~((1UL << random.Next(64)) - 1)) | (uint)random.Next(2))), exponent | random.Next(2) * 0x8000));
        foreach (Float80 value in values)
        {
            string text = Show(value);
            Assert.True(BitConverter.DoubleToInt64Bits(Float80Reference.ToDouble(value)) == BitConverter.DoubleToInt64Bits(Float80.ToDouble(value)), $"ToDouble({text})");
            Assert.True(BitConverter.SingleToInt32Bits(Float80Reference.ToSingle(value)) == BitConverter.SingleToInt32Bits(Float80.ToSingle(value)), $"ToSingle({text})");
            Assert.True(Float80Reference.ToSigned(value) == Float80.ToSigned(value), $"ToSigned({text})");
            Assert.True(Float80Reference.ToUnsigned(value) == Float80.ToUnsigned(value), $"ToUnsigned({text})");
            foreach (int width in new[] { 32, 64 })
                foreach (int away in new[] { 0, 1 })
                    Assert.True(Float80Reference.RoundToInteger(value, away, width) == Float80.RoundToInteger(value, away, width), $"RoundToInteger({text}, {away}, {width})");
            Same(Float80Reference.Floor(value), Float80.Floor(value), $"Floor({text})");
            BigInteger truncated = Float80Reference.Truncated(value);
            bool finite = (value.SignExponent & 0x7fff) != 0x7fff;
            if (finite && truncated >= (BigInteger)Int128.MinValue && truncated <= (BigInteger)Int128.MaxValue)
                Assert.True(unchecked((UInt128)(Int128)truncated) == Float80.ToWideSigned(value), $"ToWideSigned({text})");
            if (finite && truncated >= 0 && truncated <= (BigInteger)UInt128.MaxValue)
                Assert.True((UInt128)truncated == Float80.ToWideUnsigned(value), $"ToWideUnsigned({text})");
        }
        for (int index = 0; index < 200_000; index++)
        {
            long bits = random.Next(4) switch
            {
                0 => random.NextInt64() & unchecked((long)0x800fffffffffffff),
                1 => random.NextInt64() | 0x7ff0000000000000,
                _ => random.NextInt64(long.MinValue, long.MaxValue)
            };
            double number = BitConverter.Int64BitsToDouble(bits);
            Same(Float80Reference.FromDouble(number), Float80.FromDouble(number), $"FromDouble({bits:x16})");
            float single = BitConverter.Int32BitsToSingle((int)bits);
            Same(Float80Reference.FromDouble(single), Float80.FromSingle(single), $"FromSingle({(int)bits:x8})");
            long integer = random.NextInt64(long.MinValue, long.MaxValue) >> random.Next(64);
            Same(Float80Reference.FromSigned(integer), Float80.FromSigned(integer), $"FromSigned({integer})");
            Same(Float80Reference.FromUnsigned(integer), Float80.FromUnsigned(integer), $"FromUnsigned({integer})");
            UInt128 wide = new UInt128((ulong)random.NextInt64(long.MinValue, long.MaxValue), (ulong)random.NextInt64(long.MinValue, long.MaxValue)) >> random.Next(128);
            if (random.Next(2) == 0)
                wide = 0 - wide;
            Same(Float80Reference.FromWideSigned(wide), Float80.FromWideSigned(wide), $"FromWideSigned({wide:x32})");
            Same(Float80Reference.FromWideUnsigned(wide), Float80.FromWideUnsigned(wide), $"FromWideUnsigned({wide:x32})");
        }
        foreach (long integer in new[] { 0, 1, -1, long.MinValue, long.MaxValue, long.MinValue + 1 })
        {
            Same(Float80Reference.FromSigned(integer), Float80.FromSigned(integer), $"FromSigned({integer})");
            Same(Float80Reference.FromUnsigned(integer), Float80.FromUnsigned(integer), $"FromUnsigned({integer})");
        }
        foreach (UInt128 wide in new[] { UInt128.Zero, UInt128.One, UInt128.MaxValue, UInt128.One << 127, (UInt128.One << 127) - 1, UInt128.MaxValue << 63 })
        {
            Same(Float80Reference.FromWideSigned(wide), Float80.FromWideSigned(wide), $"FromWideSigned({wide:x32})");
            Same(Float80Reference.FromWideUnsigned(wide), Float80.FromWideUnsigned(wide), $"FromWideUnsigned({wide:x32})");
        }
    }

    [Fact]
    public void WideDivisionMatchesUInt128()
    {
        var random = new Random(22);
        for (int index = 0; index < 200_000; index++)
        {
            ulong divisor = (ulong)random.NextInt64() | 0x8000000000000000;
            if (random.Next(4) == 0) divisor = random.Next(2) == 0 ? 0x8000000000000000 : ulong.MaxValue - (ulong)random.Next(4);
            ulong high = random.Next(4) == 0 ? divisor - 1 - (ulong)random.Next(2) : (ulong)random.NextInt64(long.MinValue, long.MaxValue) % divisor;
            ulong low = random.Next(4) == 0 ? ulong.MaxValue - (ulong)random.Next(2) : (ulong)random.NextInt64(long.MinValue, long.MaxValue);
            UInt128 dividend = new(high, low);
            ulong quotient = Float80.DivideWide(high, low, divisor, out ulong remainder);
            Assert.True((UInt128)quotient == dividend / divisor && (UInt128)remainder == dividend % divisor, $"{dividend:x32} / {divisor:x16}");
        }
    }

    /// <summary>
    /// Fixed by PF-14: a zero significand with a nonzero exponent (a pseudo-zero, which ordered comparisons and
    /// llvm.is.fpclass already treat as zero) is a zero in arithmetic too. Before, dividing by one threw
    /// DivideByZeroException, as did a remainder by one; infinity times one gave infinity; and the conversion to double
    /// gave infinity when its exponent was large.
    /// </summary>
    [Fact]
    public void PseudoZerosAreZeros()
    {
        Float80 zero = new(0, 0x4000), negativeZero = new(0, 0xfffe), one = Value(1), infinity = Float80.Divide(one, Value(0));
        Encoded(Float80.Divide(one, zero), 0x8000000000000000, 0x7fff);
        Encoded(Float80.Divide(one, negativeZero), 0x8000000000000000, 0xffff);
        Assert.True(double.IsNaN(Float80.ToDouble(Float80.Divide(zero, negativeZero))));
        Assert.True(double.IsNaN(Float80.ToDouble(Float80.Multiply(infinity, zero))));
        Assert.True(double.IsNaN(Float80.ToDouble(Float80.Remainder(one, zero))));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(Float80.ToDouble(negativeZero)));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(Float80.ToSingle(new Float80(0, 0x7ffe))));
        Encoded(Float80.Add(zero, negativeZero), 0, 0);
    }

    /// <summary>
    /// Fixed by PF-14: conversions to i128 of values out of range (poison in LLVM) threw OverflowException; they now
    /// saturate as compiler-rt's __fixxfti and __fixunsxfti, which native code calls, do for values of 2^128 and more.
    /// </summary>
    [Fact]
    public void WideConversionsSaturate()
    {
        Float80 infinity = Float80.Divide(Value(1), Value(0)), nan = Float80.Divide(Value(0), Value(0));
        Float80 twoTo127 = new(long.MinValue, 16383 + 127), twoTo128 = new(long.MinValue, 16383 + 128);
        Assert.Equal((UInt128)Int128.MaxValue, Float80.ToWideSigned(infinity));
        Assert.Equal(unchecked((UInt128)Int128.MinValue), Float80.ToWideSigned(Float80.Negate(infinity)));
        Assert.Equal(unchecked((UInt128)Int128.MinValue), Float80.ToWideSigned(nan));
        Assert.Equal((UInt128)Int128.MaxValue, Float80.ToWideSigned(twoTo127));
        Assert.Equal(unchecked((UInt128)Int128.MinValue), Float80.ToWideSigned(Float80.Negate(twoTo127)));
        Assert.Equal(unchecked((UInt128)Int128.MinValue), Float80.ToWideSigned(Float80.Negate(twoTo128)));
        Assert.Equal(UInt128.One << 127, Float80.ToWideUnsigned(twoTo127));
        Assert.Equal(UInt128.MaxValue, Float80.ToWideUnsigned(twoTo128));
        Assert.Equal(UInt128.MaxValue, Float80.ToWideUnsigned(infinity));
        Assert.Equal(UInt128.MaxValue, Float80.ToWideUnsigned(nan));
        Assert.Equal(UInt128.Zero, Float80.ToWideUnsigned(Value(-1)));
        Assert.Equal(UInt128.Zero, Float80.ToWideUnsigned(Float80.Negate(infinity)));
    }
}
