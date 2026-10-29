using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

public sealed unsafe class ParsingTests
{
    private static (long Value, long Consumed) Integer(string text, int radix, bool unsigned = false)
    {
        using var arguments = new NativeArguments();
        nint input = arguments.Text(text);
        nint end = arguments.Allocate(8);
        long value = unsigned ? Parsing.Strtoul(input, end, radix) : Parsing.Strtol(input, end, radix);
        return (value, *(nint*)end - input);
    }

    [Fact] public void HexadecimalPrefixWithRadixZero() => Assert.Equal((-31L, 7L), Integer("  -0x1Fz", 0));
    [Fact] public void OctalPrefixWithRadixZero() => Assert.Equal((8L, 3L), Integer("010", 0));
    [Fact] public void NoDigitsLeavesEndAtStart() => Assert.Equal((0L, 0L), Integer("  +x", 10));
    [Fact] public void UnsignedMaximum() => Assert.Equal((-1L, 20L), Integer("18446744073709551615", 10, unsigned: true));
    [Fact] public void SignedOverflowSaturates() => Assert.Equal((long.MaxValue, 20L), Integer("99999999999999999999", 10));
    [Fact] public void Base36() => Assert.Equal((35L, 1L), Integer("z", 36));

    [Fact]
    public void FloatingPointStopsAtTheFirstInvalidCharacter()
    {
        using var arguments = new NativeArguments();
        nint input = arguments.Text("1e-3xyz");
        nint end = arguments.Allocate(8);
        Assert.Equal(0.001, Parsing.Strtod(input, end));
        Assert.Equal(4, *(nint*)end - input);
    }
}
