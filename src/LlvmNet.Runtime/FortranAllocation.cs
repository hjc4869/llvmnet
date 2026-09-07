using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class FortranAllocation
{
    [CExport("_FortranAAssign")]
    public static void Assign(nint destination, nint source, nint sourceFile, int line)
    {
        var target = new FortranDescriptor(destination);
        var input = new FortranDescriptor(source);
        if ((*(byte*)(destination + 23) & 1) != 0 || (*(byte*)(source + 23) & 1) != 0)
            throw new NotSupportedException("Derived-type Fortran assignment is not implemented.");
        bool allocatable = *(byte*)(destination + 22) == 2;
        bool conforms = target.Rank == input.Rank && Enumerable.Range(0, target.Rank).All(dimension => target.Extent(dimension) == input.Extent(dimension));
        if (allocatable && (target.Base == 0 || input.Rank != 0 && !conforms))
        {
            if (target.Rank != input.Rank) throw new InvalidOperationException("Fortran allocatable assignment rank mismatch.");
            CString.Free(target.Base);
            *(nint*)destination = 0;
            for (int dimension = 0; dimension < target.Rank; dimension++)
            {
                long lower = *(long*)(source + 24 + dimension * 24);
                SetBounds(destination, dimension, lower, lower + input.Extent(dimension) - 1);
            }
            Allocate(destination, 0, 0, 0, sourceFile, line, 0);
        }
        if (input.Rank != 0 && target.Elements != input.Elements)
            throw new InvalidOperationException("Fortran assignment shape mismatch.");
        bool character = target.Type == 40 && input.Type == 40;
        if (!character && (target.Type != input.Type || target.ElementBytes != input.ElementBytes))
            throw new NotSupportedException("Fortran assignment type conversion must be lowered by Flang.");
        long size = checked(input.Elements * input.ElementBytes);
        nint temporary = CString.Malloc(size);
        if (temporary == 0) throw new OutOfMemoryException();
        try
        {
            for (long index = 0; index < input.Elements; index++)
                Memory.Copy(temporary + checked((nint)(index * input.ElementBytes)), input.Address(index), input.ElementBytes);
            for (long index = 0; index < target.Elements; index++)
            {
                nint address = target.Address(index);
                if (character) NativeMemory.Fill((void*)address, (nuint)target.ElementBytes, (byte)' ');
                Memory.Copy(address, temporary + checked((nint)((input.Rank == 0 ? 0 : index) * input.ElementBytes)), Math.Min(target.ElementBytes, input.ElementBytes));
            }
        }
        finally { CString.Free(temporary); }
    }

    [CExport("_FortranAAllocatableSetBounds")]
    public static void SetBounds(nint descriptor, int dimension, long lower, long upper)
    {
        var array = new FortranDescriptor(descriptor);
        if (dimension < 0 || dimension >= array.Rank)
            throw new IndexOutOfRangeException("Fortran allocatable dimension.");
        *(long*)(descriptor + 24 + dimension * 24) = lower;
        *(long*)(descriptor + 32 + dimension * 24) = Math.Max(0, checked(upper - lower + 1));
    }

    [CExport("_FortranAAllocatableAllocate")]
    public static int Allocate(nint descriptor, nint asynchronous, int hasStatus, nint errorMessage, nint source, int line, nint copyFunction)
    {
        var array = new FortranDescriptor(descriptor);
        if (asynchronous is not (0 or -1) || (*(byte*)(descriptor + 23) & 1) != 0)
            throw new NotSupportedException("Fortran asynchronous or derived-type allocation is not implemented.");
        if (array.Base != 0)
            return Failure(12, hasStatus, "Fortran allocatable is already allocated.");
        long size = array.ElementBytes;
        try
        {
            for (int dimension = 0; dimension < array.Rank; dimension++)
            {
                *(long*)(descriptor + 40 + dimension * 24) = size;
                size = checked(size * array.Extent(dimension));
            }
            nint pointer = CString.Malloc(size);
            if (pointer == 0)
                return Failure(19, hasStatus, "Fortran allocation failed.");
            *(nint*)descriptor = pointer;
            return 0;
        }
        catch (OverflowException) { return Failure(19, hasStatus, "Fortran allocation size overflow."); }
    }

    [CExport("_FortranAAllocatableDeallocate")]
    public static int Deallocate(nint descriptor, int hasStatus, nint errorMessage, nint source, int line)
    {
        if ((*(byte*)(descriptor + 23) & 1) != 0)
            throw new NotSupportedException("Fortran derived-type finalization is not implemented.");
        nint pointer = *(nint*)descriptor;
        if (pointer == 0)
            return Failure(11, hasStatus, "Fortran allocatable is not allocated.");
        CString.Free(pointer);
        *(nint*)descriptor = 0;
        return 0;
    }

    private static int Failure(int code, int hasStatus, string text)
    {
        if (hasStatus == 0)
            throw new InvalidOperationException(text);
        return code;
    }

    [CExport("_FortranATrim")]
    public static void Trim(nint result, nint input, nint source, int line)
    {
        var text = new FortranDescriptor(input);
        if (text.Rank != 0 || text.Type != 40)
            throw new NotSupportedException("TRIM currently requires a scalar CHARACTER(kind=1).");
        long length = text.ElementBytes;
        while (length > 0 && ((byte*)text.Base)[length - 1] == ' ')
            length--;
        nint pointer = CString.Malloc(length);
        if (pointer == 0) throw new OutOfMemoryException();
        Memory.Copy(pointer, text.Base, length);
        NativeMemory.Clear((void*)result, 24);
        *(nint*)result = pointer;
        *(long*)(result + 8) = length;
        *(int*)(result + 16) = 20240719;
        *(byte*)(result + 21) = 40;
        *(byte*)(result + 22) = 2;
    }
}