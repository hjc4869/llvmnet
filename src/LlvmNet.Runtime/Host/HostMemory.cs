using System.Runtime.InteropServices;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>
/// Memory mappings (plan item LC-07) over NativeMemory. New mappings come from calloc, which hands large blocks out as
/// lazily zeroed pages, so a reservation costs no memory until it is touched, as with the kernel. File mappings are
/// copies: private ones read the file once, shared writable ones write their pages back on msync and munmap. There is
/// no memory protection, MAP_FIXED works only inside an existing mapping, and partially unmapped memory is freed once
/// its last page is unmapped. All mappings together stay within the memory .NET reports as available, so exhausting
/// the address space (as allocation-failure tests do) fails in mmap with ENOMEM while the runtime still has room.
/// </summary>
public static unsafe partial class HostSyscalls
{
    private sealed class Mapping(nint address, long length, bool shared, bool writable, OpenFile? file, long offset, nint allocation = 0)
    {
        internal readonly nint Address = address;
        internal readonly nint Allocation = allocation;  // the block to free; 0 for a replacement inside another mapping
        internal readonly long Length = length;          // a multiple of the page size
        internal readonly bool Shared = shared;
        internal readonly bool Writable = writable;
        internal readonly OpenFile? File = file;         // retained for write-back and MADV_DONTNEED; null if anonymous
        internal readonly long Offset = offset;
        internal readonly bool[] Unmapped = new bool[length / PageSize];
        internal int UnmappedPages;
        internal nint End => Address + (nint)Length;
    }

    private static readonly object mappingGate = new();
    private static readonly SortedList<nint, Mapping> mappings = [];
    private static long mappedBytes;

    private static readonly ulong mappingBudget = (ulong)Math.Max(1L << 30, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>Reserves address space for a new mapping within RLIMIT_AS, RLIMIT_DATA and the mapping budget; false means ENOMEM.</summary>
    private static bool Reserve(long size)
    {
        ulong limit = Math.Min(Math.Min(SoftLimit(RLIMIT_AS), SoftLimit(RLIMIT_DATA)), mappingBudget);
        lock (mappingGate)
        {
            if ((ulong)(mappedBytes + size) > limit) return false;
            mappedBytes += size;
            return true;
        }
    }

    private static long PageRound(long length) => (length + PageSize - 1) & ~(PageSize - 1);

    /// <summary>The mappings that overlap [address, address + length), in address order.</summary>
    private static List<Mapping> Overlapping(nint address, long length)
    {
        IList<nint> starts = mappings.Keys;
        int low = 0, high = starts.Count - 1, first = starts.Count;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (mappings.Values[middle].End > address) { first = middle; high = middle - 1; }
            else low = middle + 1;
        }
        List<Mapping> result = [];
        for (int index = first; index < starts.Count && starts[index] < address + (nint)length; index++) result.Add(mappings.Values[index]);
        return result;
    }

    /// <summary>Page-aligned, lazily zeroed memory and the block to free later; 0 when calloc fails.</summary>
    private static nint AllocatePages(long size, out nint allocation)
    {
        // NativeMemory reports failure with OutOfMemoryException, which mmap must turn into ENOMEM.
        try { allocation = (nint)NativeMemory.AllocZeroed((nuint)(size + PageSize)); }
        catch (OutOfMemoryException) { allocation = 0; }
        return allocation == 0 ? 0 : (allocation + (nint)PageSize - 1) & ~(nint)(PageSize - 1);
    }

    private static void Fill(Mapping mapping, nint address, long length, bool zeroed = false)
    {
        if (!zeroed) NativeMemory.Clear((void*)address, (nuint)length);
        if (mapping.File?.Stream is not FileStream stream) return;
        long fileOffset = mapping.Offset + (address - mapping.Address);
        long available = Math.Max(0, Math.Min(length, RandomAccess.GetLength(stream.SafeFileHandle) - fileOffset));
        for (long done = 0; done < available;)
        {
            int read = RandomAccess.Read(stream.SafeFileHandle, new Span<byte>((void*)(address + (nint)done), (int)Math.Min(available - done, int.MaxValue)), fileOffset + done);
            if (read == 0) break;
            done += read;
        }
    }

    /// <summary>Writes a shared writable mapping's pages in [address, address + length) back to its file, never past the file's end.</summary>
    private static void WriteBack(Mapping mapping, nint address, long length)
    {
        if (!mapping.Shared || !mapping.Writable || mapping.File?.Stream is not FileStream stream) return;
        long fileOffset = mapping.Offset + (address - mapping.Address);
        long count = Math.Max(0, Math.Min(length, RandomAccess.GetLength(stream.SafeFileHandle) - fileOffset));
        for (long done = 0; done < count;)
        {
            int chunk = (int)Math.Min(count - done, int.MaxValue);
            RandomAccess.Write(stream.SafeFileHandle, new ReadOnlySpan<byte>((void*)(address + (nint)done), chunk), fileOffset + done);
            done += chunk;
        }
    }

    private static long Map(nint address, long length, int protection, int flags, int descriptor, long offset)
    {
        int type = flags & (MAP_SHARED | MAP_PRIVATE);
        if (length <= 0 || (offset & (PageSize - 1)) != 0 || type is 0 or (MAP_SHARED | MAP_PRIVATE)) return -EINVAL;
        long size = PageRound(length);
        OpenFile? file = null;
        if ((flags & MAP_ANONYMOUS) == 0)
        {
            file = Lookup(descriptor);
            if (file is null) return -EBADF;
            if (file.Stream is not FileStream) return -ENODEV;
            if (!file.CanRead || type == MAP_SHARED && (protection & PROT_WRITE) != 0 && !file.CanWrite) return -EACCES;
        }
        if ((flags & (MAP_FIXED | MAP_FIXED_NOREPLACE)) != 0)
        {
            lock (mappingGate)
            {
                List<Mapping> covering = Overlapping(address, size);
                if ((flags & MAP_FIXED_NOREPLACE) != 0 && covering.Count > 0) return -EEXIST;
                // Only a range inside one existing private mapping can be replaced in place.
                if ((address & (PageSize - 1)) != 0 || covering.Count != 1 || covering[0].Address > address || covering[0].End < address + (nint)size ||
                    covering[0].Shared || type == MAP_SHARED || file is not null && covering[0].File is null)
                    return -EINVAL;
                var replacement = new Mapping(address, size, false, true, file is null ? null : Retain(file), offset);
                Fill(replacement, address, size);
                if (replacement.File is not null) Release(replacement.File);
                return address;
            }
        }
        if (!Reserve(size)) return -ENOMEM;
        nint memory = AllocatePages(size, out nint allocation);
        if (memory == 0) { lock (mappingGate) mappedBytes -= size; return -ENOMEM; }
        var mapping = new Mapping(memory, size, type == MAP_SHARED, (protection & PROT_WRITE) != 0, file is null ? null : Retain(file), offset, allocation);
        Fill(mapping, memory, size, zeroed: true);
        lock (mappingGate) mappings[memory] = mapping;
        return memory;
    }

    private static long Unmap(nint address, long length)
    {
        if ((address & (PageSize - 1)) != 0 || length <= 0) return -EINVAL;
        long size = PageRound(length);
        List<Mapping> released = [];
        lock (mappingGate)
        {
            foreach (Mapping mapping in Overlapping(address, size))
            {
                nint start = Math.Max(mapping.Address, address), end = Math.Min(mapping.End, address + (nint)size);
                WriteBack(mapping, start, end - start);
                // Unmapped pages leave the address-space budget at once, even while the rest of the block stays allocated.
                for (long page = (start - mapping.Address) / PageSize; page < (end - mapping.Address) / PageSize; page++)
                    if (!mapping.Unmapped[page]) { mapping.Unmapped[page] = true; mapping.UnmappedPages++; mappedBytes -= PageSize; }
                if (mapping.UnmappedPages == mapping.Unmapped.Length)
                {
                    mappings.Remove(mapping.Address);
                    released.Add(mapping);
                }
            }
        }
        foreach (Mapping mapping in released)
        {
            NativeMemory.Free((void*)mapping.Allocation);
            if (mapping.File is not null) Release(mapping.File);
        }
        return 0;
    }

    private static long Synchronize(nint address, long length)
    {
        if ((address & (PageSize - 1)) != 0) return -EINVAL;
        lock (mappingGate)
        {
            List<Mapping> covering = Overlapping(address, PageRound(Math.Max(length, 1)));
            if (covering.Count == 0) return -ENOMEM;
            foreach (Mapping mapping in covering)
            {
                nint start = Math.Max(mapping.Address, address), end = Math.Min(mapping.End, address + (nint)PageRound(length));
                WriteBack(mapping, start, end - start);
            }
        }
        return 0;
    }

    private static long Remap(nint address, long oldLength, long newLength, int flags)
    {
        if ((address & (PageSize - 1)) != 0 || newLength <= 0) return -EINVAL;
        long oldSize = PageRound(oldLength), newSize = PageRound(newLength);
        Mapping? mapping;
        lock (mappingGate) mapping = mappings.GetValueOrDefault(address);
        if (mapping is null || oldSize > mapping.Length) return -EFAULT;
        if (newSize <= oldSize)
        {
            if (newSize < oldSize) Unmap(address + (nint)newSize, oldSize - newSize);
            return address;
        }
        if ((flags & MREMAP_MAYMOVE) == 0) return -ENOMEM;              // growing in place is not possible
        if (!Reserve(newSize)) return -ENOMEM;
        nint memory = AllocatePages(newSize, out nint allocation);
        if (memory == 0) { lock (mappingGate) mappedBytes -= newSize; return -ENOMEM; }
        var moved = new Mapping(memory, newSize, mapping.Shared, mapping.Writable, mapping.File is null ? null : Retain(mapping.File), mapping.Offset, allocation);
        Fill(moved, memory, newSize, zeroed: true);
        Buffer.MemoryCopy((void*)address, (void*)memory, newSize, oldSize);
        lock (mappingGate) mappings[memory] = moved;
        Unmap(address, oldSize);
        return memory;
    }

    /// <summary>MADV_DONTNEED gives private pages their initial contents back, as Linux does; other advice is accepted and ignored.</summary>
    private static long Advise(nint address, long length, int advice)
    {
        if ((address & (PageSize - 1)) != 0 || length < 0) return -EINVAL;
        if (advice != MADV_DONTNEED) return 0;
        lock (mappingGate)
            foreach (Mapping mapping in Overlapping(address, PageRound(length)))
            {
                if (mapping.Shared) continue;
                nint start = Math.Max(mapping.Address, address), end = Math.Min(mapping.End, address + (nint)PageRound(length));
                Fill(mapping, start, end - start);
            }
        return 0;
    }
}
