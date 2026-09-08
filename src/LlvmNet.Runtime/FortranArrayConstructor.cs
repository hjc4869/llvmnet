using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class FortranArrayConstructor
{
    [CExport("_FortranAInitArrayConstructorVector")]
    public static void Initialize(nint vector, nint destination, int useValueLength, nint source, int line)
    {
        var array = new FortranDescriptor(destination);
        if (array.Rank != 1) throw new InvalidOperationException("Array constructor destination must have rank one.");
        if (array.Type == 42) throw new NotSupportedException("Derived-type array constructors are not implemented.");
        NativeMemory.Clear((void*)vector, 40);
        *(nint*)vector = destination;
        *(long*)(vector + 16) = array.Base == 0 ? 0 : array.Elements;
        *(nint*)(vector + 24) = source;
        *(int*)(vector + 32) = line;
        *(byte*)(vector + 36) = useValueLength != 0 ? (byte)1 : (byte)0;
    }

    private static FortranDescriptor Reserve(nint vector, long count)
    {
        nint destination = *(nint*)vector;
        var array = new FortranDescriptor(destination);
        long position = *(long*)(vector + 8);
        long capacity = *(long*)(vector + 16);
        long needed = checked(position + count);
        if (array.Base == 0 || needed > capacity)
        {
            if (*(byte*)(destination + 22) != 2) throw new InvalidOperationException("Array constructor exceeds fixed storage.");
            long requested = Math.Max(Math.Max(needed, array.Elements), Math.Max(4, checked(capacity * 2)));
            if (array.ElementBytes > 0) requested = Math.Max(requested, 128 / array.ElementBytes);
            nint memory = CString.Realloc(array.Base, Math.Max(1, checked(requested * array.ElementBytes)));
            if (memory == 0) throw new OutOfMemoryException();
            *(nint*)destination = memory;
            *(long*)(destination + 40) = array.ElementBytes;
            *(long*)(vector + 16) = requested;
        }
        FortranAllocation.SetBounds(destination, 0, 1, Math.Max(array.Elements, needed));
        return array;
    }

    [CExport("_FortranAPushArrayConstructorSimpleScalar")]
    public static void PushScalar(nint vector, nint source)
    {
        var output = Reserve(vector, 1);
        long position = *(long*)(vector + 8);
        Memory.Copy(output.Address(position), source, output.ElementBytes);
        *(long*)(vector + 8) = checked(position + 1);
    }

    [CExport("_FortranAPushArrayConstructorValue")]
    public static void PushValue(nint vector, nint source)
    {
        var input = new FortranDescriptor(source);
        nint destination = *(nint*)vector;
        var target = new FortranDescriptor(destination);
        if (target.Type != input.Type) throw new NotSupportedException("Array constructor type conversion must be lowered by Flang.");
        bool character = input.Type == 40;
        if (character && *(byte*)(vector + 36) != 0)
        {
            if (target.Base == 0) *(long*)(destination + 8) = input.ElementBytes;
            else if (target.ElementBytes != input.ElementBytes) throw new InvalidOperationException("Array constructor character lengths differ.");
        }
        if (!character && target.ElementBytes != input.ElementBytes) throw new InvalidOperationException("Array constructor element size mismatch.");
        var output = Reserve(vector, input.Elements);
        long position = *(long*)(vector + 8);
        for (long index = 0; index < input.Elements; index++)
        {
            nint address = output.Address(position + index);
            if (character) NativeMemory.Fill((void*)address, checked((nuint)output.ElementBytes), (byte)' ');
            Memory.Copy(address, input.Address(index), Math.Min(input.ElementBytes, output.ElementBytes));
        }
        *(long*)(vector + 8) = checked(position + input.Elements);
    }
}