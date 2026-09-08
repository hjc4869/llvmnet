namespace LlvmNet.Runtime;

internal readonly unsafe struct FortranDescriptor
{
    internal static int TypeCode(int category, int kind) => category switch
    {
        0 => kind switch { 1 => 7, 2 => 8, 4 => 9, 8 => 10, 16 => 11, _ => -1 },
        1 => kind switch { 1 => 45, 2 => 46, 4 => 47, 8 => 48, 16 => 49, _ => -1 },
        2 => kind switch { 2 => 25, 3 => 26, 4 => 27, 8 => 28, 10 => 29, 16 => 31, _ => -1 },
        3 => kind switch { 2 => 32, 3 => 33, 4 => 34, 8 => 35, 10 => 36, 16 => 38, _ => -1 },
        4 => kind switch { 1 => 40, 2 => 43, 4 => 44, _ => -1 },
        5 => kind switch { 1 => 12, 2 => 13, 4 => 14, 8 => 15, 16 => 16, _ => -1 },
        6 => 42,
        _ => -1
    };

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
    internal int StorageBytes
    {
        get
        {
            int bytes = 24 + Rank * 24;
            if ((*(byte*)(Handle + 23) & 1) == 0) return bytes;
            nint type = *(nint*)(Handle + bytes);
            long lengths = type == 0 ? 0 : new FortranDescriptor(type + 184).Elements;
            return checked(bytes + 8 + 8 * (int)Math.Max(1, lengths));
        }
    }
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