using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

public sealed class BuildInfoTests
{
    [Theory]
    [InlineData("0.3.0", "0.3")]
    [InlineData("0.3.7-dev+abc", "0.3")]
    [InlineData("1.2.3", "1")]
    [InlineData("2.0.0+abc", "2")]
    public void CompatibilityKeys(string version, string key) => Assert.Equal(key, BuildInfo.CompatibilityKey(version));

    [Fact]
    public void ZeroMinorReleasesAreMutuallyIncompatible() =>
        Assert.NotEqual(BuildInfo.CompatibilityKey("0.3.0"), BuildInfo.CompatibilityKey("0.4.0"));

    [Fact]
    public void RuntimeIsCompatibleWithItself() => Assert.True(BuildInfo.Compatible(BuildInfo.Version));

    [Fact]
    public void InvalidVersionsAreRejected() => Assert.Throws<FormatException>(() => BuildInfo.CompatibilityKey("next"));

    [Fact]
    public void IncompatibleImagesFailBeforeRunning() =>
        Assert.Throws<InvalidOperationException>(() => AbiContract.Require("dotnet64-v1", "9999.0.0"));
}
