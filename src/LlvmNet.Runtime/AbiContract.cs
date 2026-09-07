using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static class AbiContract
{
    private static string? active;
    public static bool Portable => active == "dotnet64-v1";
    public static bool SystemCallbacks => active == "system-linux-x64-v1";

    public static void Require(string contract)
    {
        if (IntPtr.Size != 8 || !BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("llvmnet ABI v1 requires a little-endian 64-bit .NET runtime.");
        if (contract is not ("dotnet64-v1" or "managed-host-v1" or "system-linux-x64-v1"))
            throw new NotSupportedException($"Unknown llvmnet ABI contract {contract}");
        if (contract != "dotnet64-v1" && (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64))
            throw new PlatformNotSupportedException($"{contract} targets Linux x86-64; rebuild with --runtime=portable for a platform-neutral .NET ABI.");
        string? previous = Interlocked.CompareExchange(ref active, contract, null);
        if (previous is not null && previous != contract)
            throw new InvalidOperationException($"Cannot share runtime state between incompatible llvmnet ABIs {previous} and {contract} in one process.");
    }
}