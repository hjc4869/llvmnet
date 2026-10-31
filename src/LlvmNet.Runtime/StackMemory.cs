using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

/// <summary>
/// Storage for scoped allocas (BE-17): the allocas of functions that use llvm.stacksave and llvm.stackrestore (C
/// variable-length arrays) or setjmp, which localloc cannot hold because its memory lasts until the method returns.
/// Each thread bumps a pointer through chunks of native memory. A function saves the top on entry and restores it on
/// every exit, stackrestore and longjmp restore an earlier top, so the arena unwinds as a native stack does. A mark is
/// an address in a chunk, or 0 before the thread's first allocation, so outer frames' marks stay valid while inner
/// frames add chunks. The thread's first chunk stays until the thread ends (<see cref="ReleaseThread"/>).
/// </summary>
public static unsafe class StackMemory
{
    private const nuint ChunkSize = 64 << 10;

    /// <summary>A block of arena memory; the current chunk is the newest, and older ones follow Previous.</summary>
    private sealed class Chunk(nint start, nuint size, Chunk? previous)
    {
        internal readonly nint Start = start;
        internal readonly nint End = start + (nint)size;
        internal readonly Chunk? Previous = previous;
        internal bool Contains(nint mark) => mark >= Start && mark <= End;
    }

    [ThreadStatic] private static Chunk? current;
    [ThreadStatic] private static nint top;
    [ThreadStatic] private static nint spare;           // one released chunk of ChunkSize, kept for the next growth

    /// <summary>The current top of the thread's arena, for a later <see cref="Restore"/>.</summary>
    public static nint Save() => top;

    /// <summary><paramref name="size"/> bytes at the top of the arena; the caller aligns within them.</summary>
    public static nint Allocate(nuint size)
    {
        nint start = top;
        if (current is not null && (nuint)(current.End - start) >= size)
        {
            top = start + (nint)size;
            return start;
        }
        return Grow(size);
    }

    private static nint Grow(nuint size)
    {
        nuint length = Math.Max(ChunkSize, size);
        nint memory;
        if (length == ChunkSize && spare != 0)
        {
            memory = spare;
            spare = 0;
        }
        else if ((memory = (nint)NativeMemory.Alloc(length)) == 0)
            throw new OutOfMemoryException();
        current = new Chunk(memory, length, current);
        top = memory + (nint)size;
        return memory;
    }

    /// <summary>Releases everything allocated after the mark, including whole chunks newer than the one holding it.</summary>
    public static void Restore(nint mark)
    {
        if (current is not null && current.Contains(mark))
        {
            top = mark;
            return;
        }
        Unwind(mark);
    }

    private static void Unwind(nint mark)
    {
        while (current is not null && !current.Contains(mark))
        {
            if (current.Previous is null)
            {
                // A mark of 0 was taken before the first chunk existed; the first chunk is kept, empty.
                if (mark != 0) throw new InvalidOperationException("Invalid stack restore position.");
                top = current.Start;
                return;
            }
            Release(current);
            current = current.Previous;
        }
        if (current is null && mark != 0) throw new InvalidOperationException("Invalid stack restore position.");
        top = mark;
    }

    private static void Release(Chunk chunk)
    {
        if (spare == 0 && chunk.End - chunk.Start == (nint)ChunkSize) spare = chunk.Start;
        else NativeMemory.Free((void*)chunk.Start);
    }

    /// <summary>Frees the thread's chunks when it ends (HostSyscalls.Clone), since thread-static memory is not.</summary>
    public static void ReleaseThread()
    {
        for (Chunk? chunk = current; chunk is not null; chunk = chunk.Previous) NativeMemory.Free((void*)chunk.Start);
        if (spare != 0) NativeMemory.Free((void*)spare);
        current = null;
        top = 0;
        spare = 0;
    }
}
