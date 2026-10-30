using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

/// <summary>Text and memory helpers for the C++ bridge; the C library itself is musl (portable) or glibc (system).</summary>
public static unsafe class CString
{
    public static string Text(nint pointer) => pointer == 0 ? "(null)" : Marshal.PtrToStringUTF8(pointer) ?? "";

    /// <summary>Memory from the native heap (malloc on Unix hosts), or 0 when it is exhausted.</summary>
    public static nint Malloc(long size)
    {
        if (size < 0) return 0;
        try { return (nint)NativeMemory.Alloc((nuint)Math.Max(1, size)); }
        catch (OutOfMemoryException) { return 0; }
    }

    public static nint Calloc(long count, long size)
    {
        if (count < 0 || size < 0 || count != 0 && size > long.MaxValue / count) return 0;
        try { return (nint)NativeMemory.AllocZeroed((nuint)Math.Max(1, count * size)); }
        catch (OutOfMemoryException) { return 0; }
    }

    public static void Free(nint pointer) => NativeMemory.Free((void*)pointer);
}
