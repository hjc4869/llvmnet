using System.Runtime.InteropServices;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>
/// Memory mappings over NativeMemory. Mappings of up to 128 KB (musl's malloc groups) take pages from 32 MB slabs,
/// larger ones get a block of their own; both come from calloc, which maps slabs directly as lazily zeroed pages and
/// returns them to the system on free. A page of a small mapping is one page of memory: munmap returns slab pages for
/// reuse (cleared before the next mapping gets them), and frees an empty slab when another empty one is kept; a block
/// is freed with its last page. Every slab and block records the mapping of each of its pages, so mmap and munmap cost
/// a binary search plus the pages they touch, in any order. File mappings are copies: private ones read the file once,
/// shared writable ones write their pages back on msync and munmap. There is no memory protection, and MAP_FIXED works
/// only inside an existing private mapping. Mappings stay within the memory .NET reports as available, so exhausting
/// the address space fails in mmap with ENOMEM while the runtime still has room.
/// </summary>
public static unsafe partial class HostSyscalls
{
    private sealed class Mapping(nint address, long length, bool shared, bool writable, OpenFile? file, long offset)
    {
        internal readonly nint Address = address;
        internal long Length = length;                   // a multiple of the page size; mremap may extend it in place
        internal readonly bool Shared = shared;
        internal readonly bool Writable = writable;
        internal readonly OpenFile? File = file;         // retained for write-back and MADV_DONTNEED; null if anonymous
        internal readonly long Offset = offset;
        internal long MappedPages = length / PageSize;   // the file is released with the last page
    }

    /// <summary>A page-aligned block from calloc: a slab whose pages small mappings share, or one large mapping's block.</summary>
    private sealed class Region
    {
        internal readonly nint Allocation;               // the block to free
        internal readonly nint Start, End;
        internal readonly int Pages;
        internal readonly Mapping? Only;                 // a block's mapping; null for a slab
        internal readonly Mapping?[]? Owners;            // slab: each page's mapping, null while the page is free
        internal readonly FreeSpan?[]? Spans;            // slab: the free span that starts or ends at a page
        internal readonly ulong[]? Used;                 // slab: pages handed out since calloc, cleared before reuse
        internal readonly ulong[]? Unmapped;             // block: pages already unmapped
        internal int MappedPages;

        internal Region(nint allocation, int pages, Mapping? only)
        {
            Allocation = allocation;
            Start = PageAlign(allocation);
            End = Start + (nint)pages * (nint)PageSize;
            Pages = pages;
            Only = only;
            if (only is null)
            {
                Owners = new Mapping?[pages];
                Spans = new FreeSpan?[pages];
                Used = new ulong[(pages + 63) / 64];
            }
            else
            {
                Unmapped = new ulong[(pages + 63) / 64];
                MappedPages = pages;
            }
        }

        internal nint Address(int page) => Start + (nint)page * (nint)PageSize;

        internal Mapping? Owner(int page) => Owners is not null ? Owners[page] : (Unmapped![page >> 6] & 1UL << page) == 0 ? Only : null;
    }

    /// <summary>Free pages of a slab, linked into the free list for its length.</summary>
    private sealed class FreeSpan(Region slab, int start, int pages)
    {
        internal readonly Region Slab = slab;
        internal int Start = start, Pages = pages;
        internal FreeSpan? Previous, Next;
    }

    /// <summary>One mapping's pages within one region.</summary>
    private readonly record struct PageRun(Region Region, int First, int Pages, Mapping Mapping)
    {
        internal nint Address => Region.Address(First);
        internal long Length => Pages * PageSize;
    }

    private const int SlabPages = 8192;                  // 32 MB: glibc's malloc maps every block above 32 MB directly
    private const int LargestSlabMapping = 32;           // 128 KB, mallocng's threshold for mapping an allocation alone
    private const long LargestRange = 1L << 47;          // the x86-64 user address space

    private static readonly object mappingGate = new();
    private static readonly bool freeCalled = CallFree();
    private static Region[] regions = new Region[64];    // in address order
    private static int regionCount;
    private static readonly FreeSpan?[] freeSpans = new FreeSpan?[LargestSlabMapping + 1];  // [n]: spans of n pages; [0]: longer ones
    private static int emptySlabs;
    private static long mappedBytes;

    private static readonly ulong mappingBudget = (ulong)Math.Max(1L << 30, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    /// Calls NativeMemory.Free once at start-up. Its first call would otherwise come whenever the first region is freed,
    /// and a method first called late in a run restarts the JIT's call-counting delay (see <see cref="ReleasePages"/>);
    /// for the same reason <see cref="Claim"/> clears pages with NativeMemory.Fill, which memset uses from the start.
    /// </summary>
    private static bool CallFree()
    {
        NativeMemory.Free(null);
        return true;
    }

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

    private static nint PageAlign(nint address) => (address + (nint)PageSize - 1) & ~(nint)(PageSize - 1);

    /// <summary>The index of the first region that ends after <paramref name="address"/>, or the region count.</summary>
    private static int FirstRegionAfter(nint address)
    {
        int low = 0, high = regionCount;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (regions[middle].End > address) high = middle;
            else low = middle + 1;
        }
        return low;
    }

    private static void AddRegion(Region region)
    {
        int index = FirstRegionAfter(region.Start);
        if (regionCount == regions.Length) Array.Resize(ref regions, regionCount * 2);
        Array.Copy(regions, index, regions, index + 1, regionCount - index);
        regions[index] = region;
        regionCount++;
    }

    /// <summary>The slabs and blocks that hold mappings, for tests (tests/LlvmNet.UnitTests/HostMemoryTests.cs).</summary>
    internal static int MappingRegions
    {
        get { lock (mappingGate) return regionCount; }
    }

    /// <summary>The mapping that maps the page at <paramref name="address"/>, if any.</summary>
    private static Mapping? MappingAt(nint address)
    {
        int index = FirstRegionAfter(address);
        if (index == regionCount || regions[index].Start > address) return null;
        return regions[index].Owner((int)((address - regions[index].Start) / PageSize));
    }

    /// <summary>The runs of mapped pages in the page-aligned range [address, address + length), in address order.</summary>
    private static List<PageRun> MappedRuns(nint address, long length)
    {
        List<PageRun> runs = [];
        nint end = address + (nint)length;
        for (int index = FirstRegionAfter(address); index < regionCount && regions[index].Start < end; index++)
        {
            Region region = regions[index];
            int page = (int)((Math.Max(address, region.Start) - region.Start) / PageSize);
            int last = (int)((Math.Min(end, region.End) - region.Start) / PageSize);
            while (page < last)
            {
                int first = page;
                Mapping? mapping = region.Owner(page++);
                while (page < last && region.Owner(page) == mapping) page++;
                if (mapping is not null) runs.Add(new PageRun(region, first, page - first, mapping));
            }
        }
        return runs;
    }

    private static ref FreeSpan? FreeList(int pages) => ref freeSpans[pages <= LargestSlabMapping ? pages : 0];

    private static void Insert(FreeSpan span)
    {
        ref FreeSpan? head = ref FreeList(span.Pages);
        span.Previous = null;
        span.Next = head;
        if (head is not null) head.Previous = span;
        head = span;
        span.Slab.Spans![span.Start] = span.Slab.Spans[span.Start + span.Pages - 1] = span;
    }

    private static void Remove(FreeSpan span)
    {
        if (span.Previous is not null) span.Previous.Next = span.Next;
        else FreeList(span.Pages) = span.Next;
        if (span.Next is not null) span.Next.Previous = span.Previous;
    }

    /// <summary>Takes the first <paramref name="pages"/> pages of a free span.</summary>
    private static void Take(FreeSpan span, int pages)
    {
        Remove(span);
        if (span.Pages == pages) return;
        span.Start += pages;
        span.Pages -= pages;
        Insert(span);
    }

    /// <summary>Free slab pages for a small mapping: the shortest span that fits, else a new slab; false when calloc fails.</summary>
    private static bool AllocateSlabPages(int pages, out Region slab, out int first)
    {
        FreeSpan? span = null;
        for (int length = pages; length <= LargestSlabMapping && span is null; length++) span = freeSpans[length];
        span ??= freeSpans[0];
        if (span is null)
        {
            nint allocation;
            try { allocation = (nint)NativeMemory.AllocZeroed((nuint)((SlabPages + 1) * PageSize)); }
            catch (OutOfMemoryException) { allocation = 0; }
            if (allocation == 0) { slab = null!; first = 0; return false; }
            var created = new Region(allocation, SlabPages, null);
            AddRegion(created);
            emptySlabs++;
            Insert(span = new FreeSpan(created, 0, SlabPages));
        }
        slab = span.Slab;
        first = span.Start;
        if (slab.MappedPages == 0) emptySlabs--;
        Take(span, pages);
        return true;
    }

    /// <summary>Hands free slab pages to a mapping, clearing the ones an earlier mapping used.</summary>
    private static void Claim(Region slab, int first, int pages, Mapping mapping)
    {
        int end = first + pages;
        for (int page = first; page < end;)
        {
            if ((slab.Used![page >> 6] & 1UL << page) == 0) { page++; continue; }
            int dirty = page;
            while (page < end && (slab.Used[page >> 6] & 1UL << page) != 0) page++;
            NativeMemory.Fill((void*)slab.Address(dirty), (nuint)((page - dirty) * PageSize), 0);
        }
        for (int page = first; page < end; page++)
        {
            slab.Used![page >> 6] |= 1UL << page;
            slab.Owners![page] = mapping;
        }
        slab.MappedPages += pages;
    }

    /// <summary>
    /// Unmaps pages of a region. Slab pages return to the free spans, merged with free neighbours, and an empty slab is
    /// freed when another one is already empty; a block is freed with its last page. Both kinds share this method, so
    /// the first munmap compiles every path: a method the JIT first compiles late in a run restarts its call-counting
    /// delay (100 ms) and keeps the program's hot code unoptimized that much longer.
    /// </summary>
    private static void ReleasePages(Region region, int first, int pages)
    {
        region.MappedPages -= pages;
        if (region.Owners is null)
        {
            for (int page = first; page < first + pages; page++) region.Unmapped![page >> 6] |= 1UL << page;
            if (region.MappedPages > 0) return;
        }
        else
        {
            Array.Clear(region.Owners, first, pages);
            int start = first, end = first + pages;
            FreeSpan? left = start > 0 && region.Owners[start - 1] is null ? region.Spans![start - 1] : null;
            FreeSpan? right = end < region.Pages && region.Owners[end] is null ? region.Spans![end] : null;
            if (left is not null) { Remove(left); start = left.Start; }
            if (right is not null) { Remove(right); end = right.Start + right.Pages; }
            FreeSpan span = left ?? right ?? new FreeSpan(region, start, 0);
            span.Start = start;
            span.Pages = end - start;
            Insert(span);
            if (region.MappedPages > 0 || ++emptySlabs == 1) return;
            Remove(span);
            emptySlabs--;
        }
        int index = FirstRegionAfter(region.Start);
        Array.Copy(regions, index + 1, regions, index, regionCount - index - 1);
        regions[--regionCount] = null!;
        NativeMemory.Free((void*)region.Allocation);
    }

    private static void Fill(Mapping mapping, nint address, long length, bool zeroed = false)
    {
        if (!zeroed) NativeMemory.Clear((void*)address, (nuint)length);
        long fileOffset = mapping.Offset + (address - mapping.Address);
        if (mapping.File?.Stream is MemoryStream memory)
        {
            // A synthetic file (/etc/passwd, a generated zoneinfo image) is read-only and lives in memory.
            lock (memory)
            {
                long saved = memory.Position;
                memory.Position = Math.Min(fileOffset, memory.Length);
                for (int read; length > 0 && (read = memory.Read(new Span<byte>((void*)address, (int)Math.Min(length, int.MaxValue)))) > 0; address += read, length -= read) { }
                memory.Position = saved;
            }
            return;
        }
        if (mapping.File?.Stream is not FileStream stream) return;
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

    /// <summary>
    /// A new mapping of <paramref name="size"/> bytes, filled from the file if there is one; its address or -ENOMEM. It
    /// is filled before another thread's munmap can reach it: a block before it joins the table, slab pages under the lock.
    /// </summary>
    private static long NewMapping(long size, bool shared, bool writable, OpenFile? file, long offset)
    {
        if (size / PageSize > int.MaxValue || !Reserve(size)) return -ENOMEM;
        int pages = (int)(size / PageSize);
        OpenFile? retained = file is null ? null : Retain(file);
        Mapping? mapping = null;
        try
        {
            if (pages > LargestSlabMapping)
            {
                nint allocation;
                try { allocation = (nint)NativeMemory.AllocZeroed((nuint)(size + PageSize)); }
                catch (OutOfMemoryException) { allocation = 0; }
                if (allocation == 0) return -ENOMEM;
                var block = new Region(allocation, pages, new Mapping(PageAlign(allocation), size, shared, writable, retained, offset));
                try { Fill(block.Only!, block.Start, size, zeroed: true); }
                catch { NativeMemory.Free((void*)allocation); throw; }
                lock (mappingGate) AddRegion(block);
                mapping = block.Only;
            }
            else
                lock (mappingGate)
                {
                    if (!AllocateSlabPages(pages, out Region slab, out int first)) return -ENOMEM;
                    var claimed = new Mapping(slab.Address(first), size, shared, writable, retained, offset);
                    Claim(slab, first, pages, claimed);
                    try { Fill(claimed, claimed.Address, size, zeroed: true); }
                    catch { ReleasePages(slab, first, pages); throw; }
                    mapping = claimed;
                }
            return mapping!.Address;
        }
        finally
        {
            if (mapping is null)
            {
                lock (mappingGate) mappedBytes -= size;
                if (retained is not null) Release(retained);
            }
        }
    }

    private static long Map(nint address, long length, int protection, int flags, int descriptor, long offset)
    {
        int type = flags & (MAP_SHARED | MAP_PRIVATE);
        if (length <= 0 || (offset & (PageSize - 1)) != 0 || type is 0 or (MAP_SHARED | MAP_PRIVATE)) return -EINVAL;
        if (length > LargestRange) return -ENOMEM;
        long size = PageRound(length);
        OpenFile? file = null;
        if ((flags & MAP_ANONYMOUS) == 0)
        {
            file = Lookup(descriptor);
            if (file is null) return -EBADF;
            if (file.Stream is not (FileStream or MemoryStream)) return -ENODEV;
            if (!file.CanRead || type == MAP_SHARED && (protection & PROT_WRITE) != 0 && !file.CanWrite) return -EACCES;
        }
        if ((flags & (MAP_FIXED | MAP_FIXED_NOREPLACE)) != 0)
        {
            if ((address & (PageSize - 1)) != 0) return -EINVAL;
            lock (mappingGate)
            {
                List<PageRun> covering = MappedRuns(address, size);
                if ((flags & MAP_FIXED_NOREPLACE) != 0 && covering.Count > 0) return -EEXIST;
                // Only pages of one existing private mapping can be replaced in place.
                if (covering.Count != 1 || covering[0].Address != address || covering[0].Length != size || covering[0].Mapping.Shared ||
                    type == MAP_SHARED || file is not null && covering[0].Mapping.File is null)
                    return -EINVAL;
                var replacement = new Mapping(address, size, false, true, file is null ? null : Retain(file), offset);
                Fill(replacement, address, size);
                if (replacement.File is not null) Release(replacement.File);
                return address;
            }
        }
        return NewMapping(size, type == MAP_SHARED, (protection & PROT_WRITE) != 0, file, offset);
    }

    private static long Unmap(nint address, long length)
    {
        if ((address & (PageSize - 1)) != 0 || length <= 0 || length > LargestRange) return -EINVAL;
        List<OpenFile>? files = null;
        lock (mappingGate)
            foreach (PageRun run in MappedRuns(address, PageRound(length)))
            {
                WriteBack(run.Mapping, run.Address, run.Length);
                // Unmapped pages leave the address-space budget at once; slab pages are free for the next mapping.
                mappedBytes -= run.Length;
                if ((run.Mapping.MappedPages -= run.Pages) == 0 && run.Mapping.File is not null) (files ??= []).Add(run.Mapping.File);
                ReleasePages(run.Region, run.First, run.Pages);
            }
        if (files is not null) foreach (OpenFile file in files) Release(file);
        return 0;
    }

    private static long Synchronize(nint address, long length)
    {
        if ((address & (PageSize - 1)) != 0 || length < 0 || length > LargestRange) return -EINVAL;
        lock (mappingGate)
        {
            List<PageRun> covering = MappedRuns(address, PageRound(Math.Max(length, 1)));
            if (covering.Count == 0) return -ENOMEM;
            if (length > 0) foreach (PageRun run in covering) WriteBack(run.Mapping, run.Address, run.Length);
        }
        return 0;
    }

    private static long Remap(nint address, long oldLength, long newLength, int flags)
    {
        if ((address & (PageSize - 1)) != 0 || oldLength <= 0 || newLength <= 0) return -EINVAL;
        if (oldLength > LargestRange || newLength > LargestRange) return -ENOMEM;
        long oldSize = PageRound(oldLength), newSize = PageRound(newLength);
        if (newSize / PageSize > int.MaxValue) return -ENOMEM;
        Mapping mapping;
        lock (mappingGate)
        {
            List<PageRun> covering = MappedRuns(address, oldSize);
            if (covering.Count != 1 || covering[0].Address != address || covering[0].Length != oldSize) return -EFAULT;
            mapping = covering[0].Mapping;
            if (newSize > oldSize && GrowInPlace(covering[0], (int)((newSize - oldSize) / PageSize))) return address;
        }
        if (newSize <= oldSize)
        {
            if (newSize < oldSize) Unmap(address + (nint)newSize, oldSize - newSize);
            return address;
        }
        if ((flags & MREMAP_MAYMOVE) == 0) return -ENOMEM;
        long moved = NewMapping(newSize, mapping.Shared, mapping.Writable, mapping.File, mapping.Offset + (address - mapping.Address));
        if (moved < 0) return moved;
        Buffer.MemoryCopy((void*)address, (void*)moved, newSize, oldSize);
        Unmap(address, oldSize);
        return moved;
    }

    /// <summary>Extends an anonymous mapping's run over the free slab pages that follow it, as Linux grows a mapping into free address space.</summary>
    private static bool GrowInPlace(PageRun run, int pages)
    {
        Region slab = run.Region;
        int next = run.First + run.Pages;
        if (slab.Owners is null || run.Mapping.File is not null || next >= slab.Pages || slab.Owners[next] is not null) return false;
        FreeSpan span = slab.Spans![next]!;
        if (span.Pages < pages || !Reserve(pages * PageSize)) return false;
        Take(span, pages);
        Claim(slab, next, pages, run.Mapping);
        run.Mapping.MappedPages += pages;
        run.Mapping.Length = Math.Max(run.Mapping.Length, slab.Address(next + pages) - run.Mapping.Address);
        return true;
    }

    /// <summary>The C library allocator's blocks (ports/mimalloc), by the address handed out: the calloc block and its size.</summary>
    private static readonly Dictionary<nint, (nint Allocation, long Size)> allocatorBlocks = [];

    /// <summary>
    /// __llvmnet_memory_allocate: <paramref name="size"/> zeroed bytes aligned to <paramref name="alignment"/> (a power of
    /// two) for musl's malloc, mimalloc, or null. The block comes from calloc, which leaves large blocks to the system's
    /// lazily zeroed pages, over-allocated to align it; it counts against the same limits as mappings, so malloc fails
    /// with ENOMEM where mmap would.
    /// </summary>
    [CExport("__llvmnet_memory_allocate")]
    public static nint AllocateMemory(long size, long alignment)
    {
        if (size <= 0 || alignment <= 0 || (alignment & (alignment - 1)) != 0 || size > LargestRange || alignment > LargestRange) return 0;
        long total = size + (alignment > 16 ? alignment : 0);
        if (!Reserve(total)) return 0;
        nint allocation = 0;
        try
        {
            allocation = (nint)NativeMemory.AllocZeroed((nuint)total);
            nint address = (allocation + (nint)alignment - 1) & ~((nint)alignment - 1);
            lock (mappingGate) allocatorBlocks.Add(address, (allocation, total));
            return address;
        }
        catch (OutOfMemoryException)
        {
            if (allocation != 0) NativeMemory.Free((void*)allocation);
            lock (mappingGate) mappedBytes -= total;
            return 0;
        }
    }

    /// <summary>__llvmnet_memory_free: releases a whole block from __llvmnet_memory_allocate; EINVAL for any other address.</summary>
    [CExport("__llvmnet_memory_free")]
    public static int FreeMemory(nint address)
    {
        (nint Allocation, long Size) block;
        lock (mappingGate)
        {
            if (!allocatorBlocks.Remove(address, out block)) return EINVAL;
            mappedBytes -= block.Size;
        }
        NativeMemory.Free((void*)block.Allocation);
        return 0;
    }

    /// <summary>MADV_DONTNEED gives private pages their initial contents back, as Linux does; other advice is accepted and ignored.</summary>
    private static long Advise(nint address, long length, int advice)
    {
        if ((address & (PageSize - 1)) != 0 || length < 0 || length > LargestRange) return -EINVAL;
        if (advice != MADV_DONTNEED) return 0;
        lock (mappingGate)
            foreach (PageRun run in MappedRuns(address, PageRound(length)))
                if (!run.Mapping.Shared) Fill(run.Mapping, run.Address, run.Length);
        return 0;
    }
}
