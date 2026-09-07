namespace LlvmNet.Runtime;

internal readonly unsafe struct FortranDescriptor
{
    internal readonly nint Handle;
    internal FortranDescriptor(nint handle)
    {
        Handle = handle;
        if (handle == 0 || Rank > 15)
            throw new InvalidOperationException("Invalid Flang array descriptor.");
    }
    internal nint Base => *(nint*)Handle;
    internal long ElementBytes => *(long*)(Handle + 8);
    internal int Rank => *(byte*)(Handle + 20);
    internal int Type => *(sbyte*)(Handle + 21);
    internal long Extent(int dimension) => *(long*)(Handle + 32 + dimension * 24);
    internal long Stride(int dimension) => *(long*)(Handle + 40 + dimension * 24);
    internal long Elements
    {
        get
        {
            long count = 1;
            for (int dimension = 0; dimension < Rank; dimension++)
                count = checked(count * Math.Max(0, Extent(dimension)));
            return count;
        }
    }
    internal nint Address(long index)
    {
        nint address = Base;
        for (int dimension = 0; dimension < Rank; dimension++)
        {
            long extent = Extent(dimension);
            if (extent <= 0) throw new IndexOutOfRangeException("Empty Fortran array.");
            address += checked((nint)((index % extent) * Stride(dimension)));
            index /= extent;
        }
        return address;
    }
    internal bool Logical(long index)
    {
        nint address = Address(Rank == 0 ? 0 : index);
        return ElementBytes switch
        {
            1 => *(byte*)address != 0,
            2 => *(ushort*)address != 0,
            4 => *(uint*)address != 0,
            8 => *(ulong*)address != 0,
            _ => throw new NotSupportedException($"Fortran LOGICAL kind with {ElementBytes} bytes")
        };
    }
}