using System.Buffers.Binary;

namespace LlvmNet;

internal sealed class ConstantData
{
    internal byte[] Bytes { get; }
    internal List<(long Offset, nint Value)> Relocations { get; } = [];
    private readonly TypeSystem types;

    internal ConstantData(TypeSystem types, nint value)
    {
        this.types = types;
        Bytes = new byte[checked((int)Math.Max(1, types.Size(Llvm.LLVMTypeOf(value))))];
        Write(value, 0);
    }

    private void Write(nint value, long offset)
    {
        if (Llvm.LLVMIsNull(value) != 0 || Llvm.LLVMIsUndef(value) != 0 || Llvm.LLVMIsPoison(value) != 0)
            return;
        nint type = Llvm.LLVMTypeOf(value);
        if (TypeSystem.IsAggregate(type))
        {
            for (uint index = 0; index < types.ElementCount(type); index++)
            {
                (_, long elementOffset) = types.Element(type, index);
                Write(Llvm.LLVMGetAggregateElement(value, index), checked(offset + elementOffset));
            }
            return;
        }
        Span<byte> destination = Bytes.AsSpan(checked((int)offset));
        if (Llvm.LLVMIsAConstantInt(value) != 0 || Llvm.LLVMIsAConstantFP(value) != 0)
        {
            Llvm.Bits(value).CopyTo(destination);
            return;
        }
        Relocations.Add((offset, value));
    }
}