using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class SystemAbi
{
    private static readonly ConcurrentDictionary<string, nint> libraries = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<nint, nint> variadicCallbacks = new();
    private static readonly ConcurrentDictionary<nint, nint> managedCallbacks = new();
    private static readonly ConcurrentDictionary<nint, byte> nativeVariadicFunctions = new();
    public static nint RegisterNativeVariadic(nint pointer)
    {
        nativeVariadicFunctions.TryAdd(pointer, 0);
        return pointer;
    }
    public static int IsNativeVariadic(nint pointer) => nativeVariadicFunctions.ContainsKey(pointer) ? 1 : 0;
    public static nint RegisterCallback(nint identity, nint target)
    {
        if (managedCallbacks.GetOrAdd(identity, target) != target)
            throw new InvalidOperationException("Conflicting managed callback identity.");
        return identity;
    }
    public static nint ResolveCallback(nint identity) => managedCallbacks.GetValueOrDefault(identity);
    public static nint RegisterVariadicCallback(nint identity, nint target)
    {
        if (variadicCallbacks.GetOrAdd(identity, target) != target)
            throw new InvalidOperationException("Conflicting variadic callback identity.");
        return identity;
    }
    public static nint ResolveVariadicCallback(nint identity)
    {
        if (!variadicCallbacks.TryGetValue(identity, out nint target))
            throw new NotSupportedException("Indirect native variadic calls are not supported; compile the callback to bitcode.");
        return target;
    }
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
    public static void DebugTrap()
    {
        var raise = (delegate* unmanaged[Cdecl]<int, int>)Symbol("libc.so.6", "raise");
        raise(5);
    }
}