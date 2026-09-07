using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class SystemAbi
{
    private static readonly ConcurrentDictionary<string, nint> libraries = new(StringComparer.Ordinal);
    public static nint Symbol(string library, string name)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("This assembly targets the Linux x86-64 native system ABI.");
        nint handle = libraries.GetOrAdd(library, NativeLibrary.Load);
        return NativeLibrary.GetExport(handle, name);
    }
    public static void Flush()
    {
        var fflush = (delegate* unmanaged[Cdecl]<nint, int>)Symbol("libc.so.6", "fflush");
        fflush(0);
    }
}