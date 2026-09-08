using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe partial class FortranIO
{
    [CExport("_FortranAioOutputDerivedType")]
    public static int OutputDerivedType(nint cookie, nint descriptor, nint definedIo) => TransferDerivedType(cookie, descriptor, definedIo, true);
    [CExport("_FortranAioInputDerivedType")]
    public static int InputDerivedType(nint cookie, nint descriptor, nint definedIo) => TransferDerivedType(cookie, descriptor, definedIo, false);

    private static int TransferDerivedType(nint cookie, nint descriptor, nint definedIo, bool output)
    {
        if (definedIo != 0 && *(long*)definedIo != 0)
            throw new NotSupportedException("Non-type-bound defined Fortran I/O is not implemented.");
        var array = new FortranDescriptor(descriptor);
        if (array.Type != 42) return output ? OutputDescriptor(cookie, descriptor) : InputDescriptor(cookie, descriptor);
        if ((*(byte*)(descriptor + 23) & 1) == 0)
            throw new InvalidOperationException("Derived I/O requires a type descriptor addendum.");
        nint type = *(nint*)(descriptor + 24 + array.Rank * 24);
        if (type == 0) throw new InvalidOperationException("Missing derived-type metadata.");
        bool unformatted = Get(cookie).Operation == "inquire-length" || Get(cookie).Operation.EndsWith("-unformatted", StringComparison.Ordinal);
        int binding = output ? unformatted ? 6 : 5 : unformatted ? 4 : 3;
        if ((*(uint*)(type + 424) & (1u << binding)) != 0)
            throw new NotSupportedException("Type-bound defined Fortran I/O is not implemented.");
        var components = new FortranDescriptor(type + 232);
        byte* storage = stackalloc byte[400];
        for (long element = 0; element < array.Elements; element++)
        for (long index = 0; index < components.Elements; index++)
        {
            nint component = components.Address(index);
            if (*(byte*)(component + 24) != 1)
                throw new NotSupportedException("Derived I/O of pointer, allocatable or automatic components is not implemented.");
            int category = *(byte*)(component + 25);
            int kind = *(byte*)(component + 26);
            int rank = *(byte*)(component + 27);
            if (rank > 15) throw new InvalidOperationException("Invalid derived component rank.");
            long offset = *(long*)(component + 32);
            nint derivedType = category == 6 ? *(nint*)(component + 56) : 0;
            long bytes = category switch
            {
                3 => checked(kind * 2),
                4 => checked(kind * ExplicitTypeValue(component + 40)),
                6 when derivedType != 0 => *(long*)(derivedType + 88),
                0 or 1 or 2 or 5 => kind,
                _ => throw new NotSupportedException($"Derived I/O category {category} is not implemented.")
            };
            int code = FortranDescriptor.TypeCode(category, kind);
            if (code < 0) throw new NotSupportedException($"Derived I/O kind {kind} is not implemented.");
            NativeMemory.Clear(storage, 400);
            nint temporary = (nint)storage;
            *(nint*)temporary = array.Address(element) + checked((nint)offset);
            *(long*)(temporary + 8) = bytes;
            *(int*)(temporary + 16) = 20240719;
            *(byte*)(temporary + 20) = (byte)rank;
            *(sbyte*)(temporary + 21) = (sbyte)code;
            if (category == 6)
            {
                *(byte*)(temporary + 23) = 1;
                *(nint*)(temporary + 24 + rank * 24) = derivedType;
            }
            nint bounds = *(nint*)(component + 160);
            long stride = bytes;
            for (int dimension = 0; dimension < rank; dimension++)
            {
                if (bounds == 0) throw new InvalidOperationException("Missing derived component bounds.");
                long lower = ExplicitTypeValue(bounds + dimension * 32);
                long upper = ExplicitTypeValue(bounds + dimension * 32 + 16);
                long extent = Math.Max(0, checked(upper - lower + 1));
                *(long*)(temporary + 24 + dimension * 24) = lower;
                *(long*)(temporary + 32 + dimension * 24) = extent;
                *(long*)(temporary + 40 + dimension * 24) = stride;
                stride = checked(stride * extent);
            }
            int result = category == 6 ? TransferDerivedType(cookie, temporary, definedIo, output) :
                output ? OutputDescriptor(cookie, temporary) : InputDescriptor(cookie, temporary);
            if (result == 0) return 0;
        }
        return 1;
    }

    private static long ExplicitTypeValue(nint value)
    {
        if (*(byte*)value != 2)
            throw new NotSupportedException("Deferred or parameterized derived I/O bounds are not implemented.");
        return *(long*)(value + 8);
    }
}