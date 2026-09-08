namespace LlvmNet.Runtime;

public static unsafe class FortranDerivedLifetime
{
    internal static nint CopyableType(nint descriptor)
    {
        var array = new FortranDescriptor(descriptor);
        if (array.Type != 42 || (*(byte*)(descriptor + 23) & 1) == 0) return 0;
        nint type = *(nint*)(descriptor + 24 + array.Rank * 24);
        if (type == 0 || *(byte*)(type + 430) == 0 || *(byte*)(type + 431) == 0 || *(byte*)(type + 432) == 0 ||
            new FortranDescriptor(type + 184).Elements != 0)
            return 0;
        return type;
    }

    [CExport("_FortranAInitialize")]
    public static void Initialize(nint descriptor, nint source, int line)
    {
        var array = new FortranDescriptor(descriptor);
        if (array.Base == 0 || array.Type != 42) return;
        if ((*(byte*)(descriptor + 23) & 1) == 0) throw new InvalidOperationException("Derived initialization requires type metadata.");
        nint type = *(nint*)(descriptor + 24 + array.Rank * 24);
        if (type == 0) throw new InvalidOperationException("Missing derived initialization metadata.");
        for (long index = 0; index < array.Elements; index++) InitializeElement(array.Address(index), type);
    }

    private static void InitializeElement(nint value, nint type)
    {
        if (*(byte*)(type + 429) != 0) return;
        var components = new FortranDescriptor(type + 232);
        for (long index = 0; index < components.Elements; index++)
        {
            nint component = components.Address(index);
            int genre = *(byte*)(component + 24);
            int category = *(byte*)(component + 25);
            int kind = *(byte*)(component + 26);
            int rank = *(byte*)(component + 27);
            nint address = value + checked((nint)(*(long*)(component + 32)));
            nint initialization = *(nint*)(component + 248);
            nint nested = category == 6 ? *(nint*)(component + 56) : 0;
            long bytes = category switch
            {
                3 => kind * 2,
                4 => *(byte*)(component + 40) == 2 ? checked(kind * *(long*)(component + 48)) : 0,
                6 when nested != 0 => *(long*)(nested + 88),
                0 or 1 or 2 or 5 => kind,
                _ => throw new NotSupportedException($"Derived initialization category {category} is not implemented.")
            };
            if (genre is 2 or 3)
            {
                long descriptorSize = 24 + rank * 24 + (category == 6 ? 16 : 0);
                if (initialization != 0) { Memory.Copy(address, initialization, descriptorSize); continue; }
                if (category == 6 && new FortranDescriptor(nested + 184).Elements != 0)
                    throw new NotSupportedException("Parameterized allocatable component initialization is not implemented.");
                System.Runtime.InteropServices.NativeMemory.Clear((void*)address, checked((nuint)descriptorSize));
                *(long*)(address + 8) = bytes;
                *(int*)(address + 16) = 20240719;
                *(byte*)(address + 20) = checked((byte)rank);
                int code = FortranDescriptor.TypeCode(category, kind);
                if (code < 0) throw new NotSupportedException("Derived component type is not implemented.");
                *(sbyte*)(address + 21) = (sbyte)code;
                *(byte*)(address + 22) = genre == 3 ? (byte)2 : (byte)1;
                if (category == 6)
                {
                    *(byte*)(address + 23) = 1;
                    *(nint*)(address + 24 + rank * 24) = nested;
                }
                continue;
            }
            if (genre != 1) throw new NotSupportedException("Automatic or device derived components are not implemented.");
            long count = 1;
            nint bounds = *(nint*)(component + 160);
            for (int dimension = 0; dimension < rank; dimension++)
            {
                nint lower = bounds + dimension * 32;
                nint upper = lower + 16;
                if (bounds == 0 || *(byte*)lower != 2 || *(byte*)upper != 2)
                    throw new NotSupportedException("Parameterized derived component initialization is not implemented.");
                count = checked(count * Math.Max(0, *(long*)(upper + 8) - *(long*)(lower + 8) + 1));
            }
            if (initialization != 0) Memory.Copy(address, initialization, checked(bytes * count));
            else if (category == 6)
                for (long element = 0; element < count; element++) InitializeElement(address + checked((nint)(element * bytes)), nested);
        }
        var procedures = new FortranDescriptor(type + 296);
        for (long index = 0; index < procedures.Elements; index++)
        {
            nint component = procedures.Address(index);
            *(nint*)(value + checked((nint)(*(long*)(component + 24)))) = *(nint*)(component + 32);
        }
    }

    [CExport("_FortranADestroy")]
    public static void Destroy(nint descriptor)
    {
        var array = new FortranDescriptor(descriptor);
        if (array.Base == 0 || array.Type != 42) return;
        if ((*(byte*)(descriptor + 23) & 1) == 0) throw new InvalidOperationException("Derived destruction requires type metadata.");
        nint type = *(nint*)(descriptor + 24 + array.Rank * 24);
        if (type == 0) throw new InvalidOperationException("Missing derived destruction type metadata.");
        for (long index = 0; index < array.Elements; index++) DestroyElement(array.Address(index), type);
    }

    private static void DestroyElement(nint value, nint type)
    {
        if (*(byte*)(type + 431) == 0)
            throw new NotSupportedException("Fortran derived-type final procedures are not implemented.");
        if (*(byte*)(type + 430) != 0) return;
        var components = new FortranDescriptor(type + 232);
        for (long index = 0; index < components.Elements; index++)
        {
            nint component = components.Address(index);
            int genre = *(byte*)(component + 24);
            nint address = value + checked((nint)(*(long*)(component + 32)));
            if (genre is 3 or 4)
            {
                nint allocation = *(nint*)address;
                if (allocation == 0) continue;
                Destroy(address);
                CString.Free(allocation);
                *(nint*)address = 0;
            }
            else if (genre == 1 && *(byte*)(component + 25) == 6)
            {
                nint nested = *(nint*)(component + 56);
                if (nested == 0) throw new InvalidOperationException("Missing nested derived type metadata.");
                long count = 1;
                int rank = *(byte*)(component + 27);
                nint bounds = *(nint*)(component + 160);
                for (int dimension = 0; dimension < rank; dimension++)
                {
                    nint lower = bounds + dimension * 32;
                    nint upper = lower + 16;
                    if (bounds == 0 || *(byte*)lower != 2 || *(byte*)upper != 2)
                        throw new NotSupportedException("Parameterized derived component destruction is not implemented.");
                    count = checked(count * Math.Max(0, *(long*)(upper + 8) - *(long*)(lower + 8) + 1));
                }
                long size = *(long*)(nested + 88);
                for (long element = 0; element < count; element++) DestroyElement(address + checked((nint)(element * size)), nested);
            }
            else if (genre is not (1 or 2))
                throw new NotSupportedException($"Fortran component storage genre {genre} is not implemented.");
        }
    }
}