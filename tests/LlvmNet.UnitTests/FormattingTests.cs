using System.Text;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>Expected strings were produced by glibc printf (see tests/LlvmNet.UnitTests/README.md).</summary>
public sealed class FormattingTests
{
    private static string Format(string format, params object[] values)
    {
        using var arguments = new NativeArguments(values);
        byte[] bytes = Formatting.Format(arguments.Text(format), arguments.List);
        return Encoding.UTF8.GetString(bytes);
    }

    [Fact] public void SignedAndUnsignedIntegers() => Assert.Equal("[-42|7|4294967295]", Format("[%d|%i|%u]", -42L, 7L, 4294967295u));
    [Fact] public void FlagsAndWidths() => Assert.Equal("[00042|42   |+5| 5]", Format("[%05d|%-5d|%+d|% d]", 42L, 42L, 5L, 5L));
    [Fact] public void Radixes() => Assert.Equal("[ff FF 377 0xff 010]", Format("[%x %X %o %#x %#o]", 255L, 255L, 255L, 255L, 8L));
    [Fact] public void Strings() => Assert.Equal("[hello|he|  hello|hello  ]", Format("[%s|%.2s|%7s|%-7s]", "hello", "hello", "hello", "hello"));
    [Fact] public void FloatingNotations() => Assert.Equal("[3.141590|1.235e+04|0.0001|100000|1e+06|1E-10]", Format("[%f|%.3e|%g|%g|%g|%G]", 3.14159, 12345.678, 0.0001, 100000.0, 1e6, 1e-10));
    [Fact] public void LongIntegers() => Assert.Equal("[-9223372036854775808|18446744073709551615|-1]", Format("[%lld|%llu|%ld]", long.MinValue, ulong.MaxValue, -1L));
    [Fact] public void PercentCharacterAndRounding() => Assert.Equal("[%|A| -2.8|2|0.3333333333]", Format("[%%|%c|%5.1f|%.0f|%.10f]", 'A', -2.75, 2.5, 1.0 / 3.0));
    [Fact] public void LengthModifiersTruncate() => Assert.Equal("[44|4464|12345]", Format("[%hhd|%hd|%zu]", 300L, 70000L, 12345L));
    [Fact] public void ZerosAndLargeGeneral() => Assert.Equal("[0.000000e+00|-0.000000|1.23457e+08]", Format("[%e|%f|%g]", 0.0, -0.0, 123456789.0));

    [Fact]
    public void ArgumentsBeyondRegistersUseTheOverflowArea()
    {
        object[] values = [1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 8.5, 9.5];
        Assert.Equal("1 2 3 4 5 6 7 8 1.5 2.5 3.5 4.5 5.5 6.5 7.5 8.5 9.5",
            Format("%d %d %d %d %d %d %d %d %g %g %g %g %g %g %g %g %g", values));
    }
}
