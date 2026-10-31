using System.Runtime.InteropServices;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>Startup diagnostics for hosts that cannot run an image (XP-06): each names the problem and what to do.</summary>
public sealed class AbiContractTests
{
    private static string? Diagnose(string contract = "dotnet64-v2", int bits = 64, bool littleEndian = true,
        Architecture architecture = Architecture.X64, string framework = ".NET 10.0.14", string version = "10.0.14", bool mono = false, bool linux = true) =>
        AbiContract.Diagnose(contract, bits, littleEndian, architecture, framework, new Version(version), mono, linux);

    [Fact]
    public void SupportedHostsHaveNoDiagnostic()
    {
        Assert.Null(Diagnose());
        Assert.Null(Diagnose(architecture: Architecture.Arm64, linux: false));
        Assert.Null(Diagnose(contract: "system-linux-x64-v1"));
        Assert.Null(Diagnose(version: "11.0.0", framework: ".NET 11.0.0"));
    }

    [Fact]
    public void ThirtyTwoBitRuntimesNameTheArchitectureAndTheFix()
    {
        string? problem = Diagnose(bits: 32, architecture: Architecture.X86);
        Assert.Contains("32-bit X86", problem);
        Assert.Contains("x64 or Arm64 .NET 10 runtime", problem);
    }

    [Fact]
    public void MonoOldRuntimesAndBigEndianHostsAreNamed()
    {
        Assert.Contains("Mono", Diagnose(mono: true, framework: "Mono 6.12"));
        Assert.Contains(".NET 10 or later", Diagnose(version: "8.0.0", framework: ".NET 8.0.0"));
        Assert.Contains("big-endian", Diagnose(littleEndian: false, architecture: Architecture.S390x));
    }

    [Fact]
    public void SystemAbiNeedsLinuxX64()
    {
        Assert.Contains("--runtime=portable", Diagnose(contract: "system-linux-x64-v1", linux: false));
        Assert.Contains("--runtime=portable", Diagnose(contract: "system-linux-x64-v1", architecture: Architecture.Arm64));
        Assert.Contains("Unknown llvmnet ABI contract", Diagnose(contract: "dotnet64-v9"));
    }

    [Fact]
    public void ThisHostIsSupported() => Assert.Null(AbiContract.Diagnose("dotnet64-v2"));
}
