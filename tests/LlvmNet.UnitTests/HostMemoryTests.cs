using System.Diagnostics;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>
/// mmap, munmap, mremap and madvise as the host layer emulates them for musl (PF-16): slab pages for small mappings,
/// blocks for large ones, partial unmapping, MAP_FIXED inside a mapping, and cost independent of the unmapping order.
/// </summary>
public sealed unsafe class HostMemoryTests
{
    private const long SysMmap = 9, SysMunmap = 11, SysMremap = 25, SysMadvise = 28;
    private const int ReadWrite = 3, Private = 2, Fixed = 0x10, Anonymous = 0x20, FixedNoReplace = 0x100000;
    private const int MayMove = 1, DontNeed = 4;
    private const long EINVAL = 22, ENOMEM = 12, EEXIST = 17, EFAULT = 14;
    private const int Page = 4096;

    private static long Map(long length, int flags = Private | Anonymous, nint address = 0) =>
        HostSyscalls.Syscall(SysMmap, address, length, ReadWrite, flags, -1, 0);

    private static byte* MapPages(int pages)
    {
        long address = Map((long)pages * Page);
        Assert.True(address > 0, $"mmap failed with {address}");
        Assert.Equal(0, address % Page);
        return (byte*)address;
    }

    private static long Unmap(byte* address, long length) => HostSyscalls.Syscall(SysMunmap, (nint)address, length, 0, 0, 0, 0);

    private static long Remap(byte* address, long oldLength, long newLength, int flags) =>
        HostSyscalls.Syscall(SysMremap, (nint)address, oldLength, newLength, flags, 0, 0);

    private static void AssertZero(byte* address, long length)
    {
        for (long index = 0; index < length; index++) Assert.True(address[index] == 0, $"byte {index} is {address[index]}");
    }

    [Fact]
    public void OnePageMappingsAreDistinctZeroedAndReusedZeroed()
    {
        var pages = new List<nint>();
        for (int index = 0; index < 1000; index++)
        {
            byte* page = MapPages(1);
            AssertZero(page, Page);
            new Span<byte>(page, Page).Fill((byte)(index + 1));
            pages.Add((nint)page);
        }
        Assert.Equal(pages.Count, pages.Distinct().Count());
        for (int index = 0; index < pages.Count; index++) Assert.Equal((byte)(index + 1), ((byte*)pages[index])[Page - 1]);
        foreach (nint page in pages) Assert.Equal(0, Unmap((byte*)page, Page));
        // The freed pages are handed out again, cleared.
        var again = new List<nint>();
        for (int index = 0; index < 1000; index++)
        {
            byte* page = MapPages(1);
            AssertZero(page, Page);
            again.Add((nint)page);
        }
        Assert.NotEmpty(again.Intersect(pages));
        foreach (nint page in again) Assert.Equal(0, Unmap((byte*)page, Page));
    }

    /// <summary>Maps one-page mappings and unmaps them in ascending address order.</summary>
    private static TimeSpan MapAndUnmapAscending(int count)
    {
        var pages = new nint[count];
        var clock = Stopwatch.StartNew();
        for (int index = 0; index < count; index++) pages[index] = (nint)MapPages(1);
        Array.Sort(pages);
        foreach (nint page in pages) Assert.Equal(0, Unmap((byte*)page, Page));
        return clock.Elapsed;
    }

    [Fact]
    public void UnmappingCostGrowsLinearly()
    {
        MapAndUnmapAscending(10_000);
        TimeSpan small = MapAndUnmapAscending(50_000), large = MapAndUnmapAscending(200_000);
        // A sorted table that shifts on every removal took 0.20 s and 3.0 s (15 times as long for 4 times the mappings).
        Assert.True(large < small * 8 + TimeSpan.FromSeconds(0.5), $"50,000 mappings took {small}, 200,000 took {large}");
    }

    [Fact]
    public void EmptySlabsAreFreedExceptOne()
    {
        int before = HostSyscalls.MappingRegions;
        var pages = new List<nint>();
        for (int index = 0; index < 4 * 8192; index++) pages.Add((nint)MapPages(1));
        Assert.True(HostSyscalls.MappingRegions >= before + 2, $"{HostSyscalls.MappingRegions} regions, {before} before");
        foreach (nint page in pages) Assert.Equal(0, Unmap((byte*)page, Page));
        Assert.True(HostSyscalls.MappingRegions <= before + 1, $"{HostSyscalls.MappingRegions} regions, {before} before");
    }

    [Theory]
    [InlineData(16)]       // slab pages
    [InlineData(1024)]     // a block of its own
    public void PartialUnmapKeepsTheRestAndUnmapsAcrossTheHole(int pages)
    {
        byte* mapping = MapPages(pages);
        new Span<byte>(mapping, pages * Page).Fill(7);
        Assert.Equal(0, Unmap(mapping + 4 * Page, 4 * Page));
        Assert.Equal(7, mapping[0]);
        Assert.Equal(7, mapping[pages * Page - 1]);
        Assert.Equal(0, HostSyscalls.Syscall(SysMadvise, (nint)mapping, Page, DontNeed, 0, 0, 0));
        AssertZero(mapping, Page);
        Assert.Equal(7, mapping[Page]);
        Assert.Equal(0, Unmap(mapping, (long)pages * Page));
        Assert.Equal(-EFAULT, Remap(mapping, Page, 2 * Page, MayMove));
    }

    [Fact]
    public void FixedMappingsReplacePagesOfOneMappingOnly()
    {
        byte* first = MapPages(8);
        new Span<byte>(first, 8 * Page).Fill(5);
        Assert.Equal((long)(first + 2 * Page), Map(2 * Page, Private | Anonymous | Fixed, (nint)(first + 2 * Page)));
        AssertZero(first + 2 * Page, 2 * Page);
        Assert.Equal(5, first[4 * Page]);
        Assert.Equal(-EEXIST, Map(Page, Private | Anonymous | FixedNoReplace, (nint)first));
        Assert.Equal(-EINVAL, Map(Page, Private | Anonymous | Fixed, (nint)(first + 1)));
        Assert.Equal(0, Unmap(first + 6 * Page, Page));
        Assert.Equal(-EINVAL, Map(4 * Page, Private | Anonymous | Fixed, (nint)(first + 4 * Page)));
        Assert.Equal(0, Unmap(first, 8 * Page));
    }

    [Fact]
    public void RemapGrowsInPlaceMovesAndShrinks()
    {
        byte* blocker = MapPages(1);
        byte* mapping = MapPages(4);
        new Span<byte>(mapping, 4 * Page).Fill(9);
        byte* after = MapPages(1);
        if (after == mapping + 4 * Page)
        {
            Assert.Equal(-ENOMEM, Remap(mapping, 4 * Page, 6 * Page, 0));
            Assert.Equal(0, Unmap(after, Page));
            Assert.Equal((long)mapping, Remap(mapping, 4 * Page, 6 * Page, 0));
            AssertZero(mapping + 4 * Page, 2 * Page);
            Assert.Equal(9, mapping[4 * Page - 1]);
        }
        else
        {
            Assert.Equal(0, Unmap(after, Page));
            Assert.Equal((long)mapping, Remap(mapping, 4 * Page, 6 * Page, MayMove));
        }
        long moved = Remap(mapping, 6 * Page, 512 * Page, MayMove);
        Assert.True(moved > 0);
        mapping = (byte*)moved;
        Assert.Equal(9, mapping[0]);
        Assert.Equal(9, mapping[4 * Page - 1]);
        AssertZero(mapping + 6 * Page, 506L * Page);
        Assert.Equal(moved, Remap(mapping, 512 * Page, Page, 0));
        Assert.Equal(9, mapping[Page - 1]);
        Assert.Equal(0, Unmap(mapping, 512 * Page));
        Assert.Equal(0, Unmap(blocker, Page));
    }

    [Fact]
    public void RemapBeyondThePageCountFailsWithoutTouchingTheMapping()
    {
        byte* mapping = MapPages(1);
        mapping[0] = 3;
        // Page counts that wrap to 1, 0 and -1 in 32 bits.
        foreach (long length in new[] { (1L << 44) + 2 * Page, (1L << 44) + Page, 1L << 44 })
        {
            Assert.Equal(-ENOMEM, Remap(mapping, Page, length, 0));
            Assert.Equal(-ENOMEM, Remap(mapping, Page, length, MayMove));
        }
        Assert.Equal(3, mapping[0]);
        byte* next = MapPages(1);
        Assert.True(next != mapping);
        AssertZero(next, Page);
        Assert.Equal(0, Unmap(next, Page));
        Assert.Equal(0, Unmap(mapping, Page));
    }

    [Fact]
    public void InvalidRequestsFail()
    {
        Assert.Equal(-EINVAL, Map(0));
        Assert.Equal(-ENOMEM, Map(1L << 50));
        Assert.Equal(-EINVAL, Unmap((byte*)Page + 1, Page));
        Assert.Equal(-EINVAL, Unmap((byte*)Page, 0));
        Assert.Equal(0, Unmap((byte*)Page, Page));
    }

    // The C library allocator's blocks (__llvmnet_memory_allocate and __llvmnet_memory_free, PF-25).
    [Theory]
    [InlineData(1, 16)]
    [InlineData(100_000, 4096)]
    [InlineData(64L << 20, 65536)]
    [InlineData(1L << 20, 256L << 20)]
    public void AllocatorBlocksAreZeroedAlignedAndFreedWhole(long size, long alignment)
    {
        nint block = HostSyscalls.AllocateMemory(size, alignment);
        Assert.NotEqual(0, block);
        Assert.Equal(0, block % alignment);
        long checkedBytes = Math.Min(size, 1L << 20);
        AssertZero((byte*)block, checkedBytes);
        AssertZero((byte*)block + size - Math.Min(size, Page), Math.Min(size, Page));
        new Span<byte>((byte*)block, (int)checkedBytes).Fill(0xa5);
        Assert.Equal(0, HostSyscalls.FreeMemory(block));
        Assert.Equal(EINVAL, HostSyscalls.FreeMemory(block));
    }

    [Fact]
    public void AllocatorRefusesInvalidRequests()
    {
        Assert.Equal(0, HostSyscalls.AllocateMemory(0, 16));
        Assert.Equal(0, HostSyscalls.AllocateMemory(Page, 0));
        Assert.Equal(0, HostSyscalls.AllocateMemory(Page, 48));
        Assert.Equal(0, HostSyscalls.AllocateMemory(1L << 48, 16));
        Assert.Equal(EINVAL, HostSyscalls.FreeMemory(0));
        nint block = HostSyscalls.AllocateMemory(Page, Page);
        Assert.Equal(EINVAL, HostSyscalls.FreeMemory(block + 16));
        Assert.Equal(0, HostSyscalls.FreeMemory(block));
    }
}
