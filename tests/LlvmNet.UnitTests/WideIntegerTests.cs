using System.Runtime.InteropServices;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

public sealed unsafe class WideIntegerTests
{
    [Fact]
    public void ByteSwapUsesTheValueWidth()
    {
        Assert.Equal((UInt128)0x0201, WideInteger.ByteSwap(0x0102, 16));
        Assert.Equal(new UInt128(0x0f0e0d0c0b0a0908, 0x0706050403020100), WideInteger.ByteSwap(new UInt128(0x0001020304050607, 0x08090a0b0c0d0e0f), 128));
    }

    [Theory]
    [InlineData(0xff, 8, -1)]
    [InlineData(0x7f, 8, 127)]
    [InlineData(0x1, 1, -1)]
    [InlineData(0xfffff, 20, -1)]
    public void SignExtension(long bits, int width, long expected) =>
        Assert.Equal(expected, WideInteger.Low(WideInteger.ExtendSign((UInt128)(ulong)bits, width)));

    [Fact]
    public void CountsAreBoundedByWidth()
    {
        Assert.Equal((UInt128)3, WideInteger.CountBits(0b1011, 80, 0));
        Assert.Equal((UInt128)76, WideInteger.CountBits(0b1011, 80, 1));
        Assert.Equal((UInt128)80, WideInteger.CountBits(0, 80, 2));
        Assert.Equal((UInt128)80, WideInteger.CountBits(0, 80, 1));
    }

    [Fact]
    public void OddWidthStoresTouchOnlyTheirBytes()
    {
        nint memory = (nint)NativeMemory.Alloc(16);
        try
        {
            new Span<byte>((void*)memory, 16).Fill(0xaa);
            WideInteger.Write(memory, UInt128.MaxValue, 72);
            byte[] bytes = new Span<byte>((void*)memory, 16).ToArray();
            Assert.All(bytes[..9], value => Assert.Equal(0xff, value));
            Assert.All(bytes[9..], value => Assert.Equal(0xaa, value));
            Assert.Equal((UInt128.One << 72) - 1, WideInteger.Read(memory, 72));
        }
        finally { NativeMemory.Free((void*)memory); }
    }

    [Fact]
    public void PackedLanesRoundTrip()
    {
        nint memory = (nint)NativeMemory.AllocZeroed(8);
        try
        {
            for (int lane = 0; lane < 16; lane++) WideInteger.WritePacked(memory, lane, (UInt128)(lane % 2), 1);
            Assert.Equal(0xaaaa, *(ushort*)memory);
            for (int lane = 0; lane < 16; lane++) Assert.Equal((UInt128)(lane % 2), WideInteger.ReadPacked(memory, lane, 1));
        }
        finally { NativeMemory.Free((void*)memory); }
    }
}
