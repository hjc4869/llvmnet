namespace LlvmNet;

internal sealed class ConstantData
{
    internal const int ChunkSize = 1 << 20;

    internal long Size { get; }
    // Only chunks that receive a nonzero byte are stored, keyed by chunk index, so zero-filled parts of
    // large globals cost nothing and globals beyond 2 GB do not need a single array.
    internal SortedDictionary<long, byte[]> Chunks { get; } = [];
    internal List<(long Offset, nint Value)> Relocations { get; } = [];
    private readonly TypeSystem types;

    internal ConstantData(TypeSystem types, nint value)
    {
        this.types = types;
        Size = Math.Max(1, types.Size(Llvm.LLVMTypeOf(value)));
        Write(value, 0);
    }

    private void Write(nint value, long offset)
    {
        if (Llvm.LLVMIsNull(value) != 0 || Llvm.LLVMIsUndef(value) != 0 || Llvm.LLVMIsPoison(value) != 0)
            return;
        nint type = Llvm.LLVMTypeOf(value);
        if (TypeSystem.IsAggregate(type))
        {
            if (TypeSystem.Kind(type) == 13 && TypeSystem.Width(Llvm.LLVMGetElementType(type)) is int width &&
                width > 0 && width is not (8 or 16 or 32 or 64 or 128))
            {
                for (uint index = 0; index < types.ElementCount(type); index++)
                {
                    nint element = Llvm.LLVMGetAggregateElement(value, index);
                    if (Llvm.LLVMIsNull(element) != 0 || Llvm.LLVMIsUndef(element) != 0 || Llvm.LLVMIsPoison(element) != 0)
                        continue;
                    byte[] bits = Llvm.Bits(element);
                    for (int bit = 0; bit < width; bit++)
                    {
                        long position = checked((long)index * width + bit);
                        Or(checked(offset + position / 8), (byte)(((bits[bit / 8] >> (bit % 8)) & 1) << (int)(position % 8)));
                    }
                }
                return;
            }
            for (uint index = 0; index < types.ElementCount(type); index++)
            {
                (_, long elementOffset) = types.Element(type, index);
                Write(Llvm.LLVMGetAggregateElement(value, index), checked(offset + elementOffset));
            }
            return;
        }
        if (Llvm.LLVMIsAConstantInt(value) != 0 || Llvm.LLVMIsAConstantFP(value) != 0)
        {
            Copy(offset, Llvm.Bits(value));
            return;
        }
        Relocations.Add((offset, value));
    }

    private byte[] Chunk(long offset)
    {
        long index = offset / ChunkSize;
        if (!Chunks.TryGetValue(index, out byte[]? chunk))
            Chunks[index] = chunk = new byte[Math.Min(ChunkSize, Size - index * ChunkSize)];
        return chunk;
    }

    private void Or(long offset, byte bits)
    {
        if (bits != 0)
            Chunk(offset)[offset % ChunkSize] |= bits;
    }

    private void Copy(long offset, ReadOnlySpan<byte> bytes)
    {
        if (!bytes.ContainsAnyExcept((byte)0))
            return;
        while (!bytes.IsEmpty)
        {
            byte[] chunk = Chunk(offset);
            int start = (int)(offset % ChunkSize);
            int count = Math.Min(bytes.Length, chunk.Length - start);
            bytes[..count].CopyTo(chunk.AsSpan(start));
            offset += count;
            bytes = bytes[count..];
        }
    }
}
