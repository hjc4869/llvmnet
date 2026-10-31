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
}
