using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static class AbiContract
{
    private static string? active;
    public static bool Portable => active == "dotnet64-v2";
    public static bool SystemCallbacks => active == "system-linux-x64-v1";

    /// <summary>Called from every emitted image's type initializer before any translated code runs.</summary>
    public static void Require(string contract, string compilerVersion)
    {
        if (!BuildInfo.Compatible(compilerVersion)) throw new InvalidOperationException(VersionProblem(compilerVersion));
        Require(contract);
    }

    private static string VersionProblem(string compilerVersion) =>
        $"This program was compiled by llvmnet {compilerVersion}, but the loaded LlvmNet.Runtime is {BuildInfo.Version}. " +
        "Recompile the program, or ship the LlvmNet.Runtime.dll that the compiler copied next to it.";

    public static void Require(string contract)
    {
        if (Diagnose(contract) is string problem) throw new PlatformNotSupportedException(problem);
        string? previous = Interlocked.CompareExchange(ref active, contract, null);
        if (previous is not null && previous != contract)
            throw new InvalidOperationException($"Cannot share runtime state between incompatible llvmnet ABIs {previous} and {contract} in one process.");
    }

    /// <summary>
    /// Why this host cannot run an image with the contract, as a message that says what to do, or null when it can.
    /// The emitted entry point prints it and exits before the image's own initializer runs, so an unsupported host
    /// reports one line instead of a type-initialization failure.
    /// </summary>
    public static string? Diagnose(string contract, string? compilerVersion = null)
    {
        if (compilerVersion is not null && !BuildInfo.Compatible(compilerVersion)) return VersionProblem(compilerVersion);
        return Diagnose(contract, IntPtr.Size * 8, BitConverter.IsLittleEndian, RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.FrameworkDescription, Environment.Version, Type.GetType("Mono.Runtime") is not null,
            OperatingSystem.IsLinux());
    }

    /// <summary>Prints a startup diagnostic the way the emitted entry point reports it.</summary>
    public static void Report(string problem) => Console.Error.WriteLine("llvmnet: " + problem);

    internal static string? Diagnose(string contract, int pointerBits, bool littleEndian, Architecture architecture,
        string framework, Version version, bool mono, bool linux)
    {
        if (contract is not ("dotnet64-v2" or "system-linux-x64-v1"))
            return $"Unknown llvmnet ABI contract {contract}; this LlvmNet.Runtime ({BuildInfo.Version}) does not implement it.";
        if (mono)
            return $"llvmnet programs need the .NET runtime (CoreCLR or NativeAOT); this process runs on Mono ({framework}). Run it with 'dotnet program.dll' on .NET 10 or later.";
        if (pointerBits != 64)
            return $"llvmnet programs need a 64-bit .NET runtime; this is a {pointerBits}-bit {architecture} runtime ({framework}). Run the program with the x64 or Arm64 .NET 10 runtime ('dotnet --list-runtimes' shows the installed ones).";
        if (!littleEndian)
            return $"llvmnet programs need a little-endian host; {architecture} is big-endian.";
        if (version.Major < 10)
            return $"llvmnet programs need .NET 10 or later; this is {framework}. Install the .NET 10 runtime or set DOTNET_ROOT to one.";
        if (contract != "dotnet64-v2" && (!linux || architecture != Architecture.X64))
            return $"{contract} targets Linux x86-64; rebuild with --runtime=portable for a platform-neutral .NET ABI.";
        return null;
    }
}
