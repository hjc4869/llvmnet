using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class Memory
{
    public static nint Allocate(long size, int alignment)
    {
        nuint actualAlignment = (nuint)Math.Max(alignment, IntPtr.Size);
        nuint actualSize = checked((nuint)Math.Max(size, 1));
        actualSize = checked(actualSize + actualAlignment - 1) & ~(actualAlignment - 1);
        void* pointer = NativeMemory.AlignedAlloc(actualSize, actualAlignment);
        if (pointer is null)
            throw new OutOfMemoryException();
        NativeMemory.Clear(pointer, actualSize);
        return (nint)pointer;
    }

    // Storage for a global variable, which lives as long as the process and is never freed. calloc leaves
    // large blocks to the operating system's lazily zeroed pages, as an ELF .bss would, so a large
    // zero-initialized array costs no memory or startup time until it is touched.
    public static nint AllocateGlobal(long size, int alignment)
    {
        nuint actualAlignment = (nuint)Math.Max(alignment, 16);
        nuint actualSize = checked((nuint)Math.Max(size, 1) + actualAlignment - 1);
        void* pointer = NativeMemory.AllocZeroed(actualSize);
        if (pointer is null)
            throw new OutOfMemoryException();
        return (nint)(((nuint)pointer + actualAlignment - 1) & ~(actualAlignment - 1));
    }

    public static nint Copy(nint destination, nint source, long count)
    {
        NativeMemory.Copy((void*)source, (void*)destination, checked((nuint)count));
        return destination;
    }

    public static nint Set(nint destination, int value, long count)
    {
        NativeMemory.Fill((void*)destination, checked((nuint)count), (byte)value);
        return destination;
    }

    public static void Unreachable() => throw new InvalidOperationException("Executed LLVM unreachable instruction.");
}
