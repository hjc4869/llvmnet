using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public sealed unsafe class StackMemory : IDisposable
{
    private readonly List<nint> allocations = [];

    public nint Allocate(nuint size)
    {
        nint allocation = (nint)NativeMemory.Alloc(size == 0 ? 1 : size);
        if (allocation == 0) throw new OutOfMemoryException();
        try { allocations.Add(allocation); }
        catch { NativeMemory.Free((void*)allocation); throw; }
        return allocation;
    }

    public nint Save() => allocations.Count;

    public void Restore(nint saved)
    {
        if (saved < 0 || saved > allocations.Count)
            throw new InvalidOperationException("Invalid stack restore position.");
        int count = (int)saved;
        for (int index = allocations.Count - 1; index >= count; index--)
            NativeMemory.Free((void*)allocations[index]);
        allocations.RemoveRange(count, allocations.Count - count);
    }

    public void Dispose() => Restore(0);
}