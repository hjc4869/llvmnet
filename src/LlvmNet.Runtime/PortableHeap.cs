using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

internal static unsafe class PortableHeap
{
    internal static nint Allocate(long size, long alignment = 16)
    {
        if (size < 0 || alignment < 8 || (alignment & (alignment - 1)) != 0) return 0;
        try
        {
            nuint bytes = checked((nuint)size + (nuint)alignment + 16);
            nint allocation = (nint)NativeMemory.Alloc(bytes);
            nuint aligned = ((nuint)allocation + 16 + (nuint)alignment - 1) & ~((nuint)alignment - 1);
            nint pointer = (nint)aligned;
            ((nint*)pointer)[-2] = allocation;
            ((long*)pointer)[-1] = size;
            return pointer;
        }
        catch (Exception error) when (error is OutOfMemoryException or OverflowException) { return 0; }
    }
    internal static void Free(nint pointer)
    {
        if (pointer != 0) NativeMemory.Free((void*)((nint*)pointer)[-2]);
    }
    internal static nint Reallocate(nint pointer, long size)
    {
        if (pointer == 0) return Allocate(size);
        if (size == 0) { Free(pointer); return 0; }
        nint result = Allocate(size);
        if (result == 0) return 0;
        NativeMemory.Copy((void*)pointer, (void*)result, (nuint)Math.Min(size, ((long*)pointer)[-1]));
        Free(pointer);
        return result;
    }
}