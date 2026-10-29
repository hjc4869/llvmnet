namespace LlvmNet.UnitTests;

/// <summary>Option combinations the driver must reject before invoking any frontend.</summary>
public sealed class DriverTests
{
    [Theory]
    [InlineData("--nativeaot", "-c", "input.c")]
    [InlineData("--aot-debug", "input.c")]
    [InlineData("--runtime=invalid", "input.c")]
    [InlineData("--runtime=portable", "--native-import", "abs=libc.so.6!abs", "input.c")]
    [InlineData("--runtime=portable", "--system-library", "libc.so.6", "input.c")]
    [InlineData("--runtime=portable", "--trap-missing-arguments", "input.c")]
    [InlineData("--runtime=system", "--nativeaot", "--runtime-id", "win-x64", "input.c")]
    [InlineData("--aot-instruction-set", "fastest", "input.c")]
    [InlineData("--native-import", "missing-separator", "input.c")]
    public void InvalidCombinationsAreRejected(params string[] arguments) =>
        Assert.Throws<ArgumentException>(() => Driver.Run(arguments));

    [Fact]
    public void NativeLinkerOptionsAreNotSilentlyAccepted() =>
        Assert.Throws<NotSupportedException>(() => Driver.Run(["-Wl,-rpath,/opt", "input.c"]));
}
