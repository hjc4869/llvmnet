using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class FortranTransform
{
    [CExport("_FortranATransfer")]
    public static void Transfer(nint result, nint source, nint mold, nint sourceFile, int line) => TransferCore(result, source, mold, null, sourceFile, line);
    [CExport("_FortranATransferSize")]
    public static void TransferSize(nint result, nint source, nint mold, nint sourceFile, int line, long size) => TransferCore(result, source, mold, size, sourceFile, line);

    private static void TransferCore(nint result, nint source, nint mold, long? requested, nint sourceFile, int line)
    {
        var input = new FortranDescriptor(source);
        var model = new FortranDescriptor(mold);
        if ((*(byte*)(source + 23) & 1) != 0 || (*(byte*)(mold + 23) & 1) != 0)
            throw new NotSupportedException("Derived-type TRANSFER is not implemented.");
        long inputBytes = checked(input.Elements * input.ElementBytes);
        int rank = requested is not null || model.Rank != 0 ? 1 : 0;
        long elements = requested ?? (rank == 0 ? 1 : model.ElementBytes == 0 ? 0 : checked((inputBytes + model.ElementBytes - 1) / model.ElementBytes));
        if (elements < 0) throw new InvalidOperationException("TRANSFER SIZE is negative.");
        NativeMemory.Clear((void*)result, checked((nuint)(24 + rank * 24)));
        *(long*)(result + 8) = model.ElementBytes;
        *(int*)(result + 16) = 20240719;
        *(byte*)(result + 20) = (byte)rank;
        *(sbyte*)(result + 21) = (sbyte)model.Type;
        *(byte*)(result + 22) = 2;
        if (rank == 1) FortranAllocation.SetBounds(result, 0, 1, elements);
        FortranAllocation.Allocate(result, 0, 0, 0, sourceFile, line, 0);
        var output = new FortranDescriptor(result);
        long outputBytes = checked(output.Elements * output.ElementBytes);
        NativeMemory.Clear((void*)output.Base, checked((nuint)outputBytes));
        long copied = 0;
        for (long index = 0; index < input.Elements && copied < outputBytes; index++)
        {
            long bytes = Math.Min(input.ElementBytes, outputBytes - copied);
            Memory.Copy(output.Base + checked((nint)copied), input.Address(index), bytes);
            copied += bytes;
        }
    }

    [CExport("_FortranAReshape")]
    public static void Reshape(nint result, nint source, nint shape, nint pad, nint order, nint sourceFile, int line)
    {
        var input = new FortranDescriptor(source);
        var dimensions = new FortranDescriptor(shape);
        if (dimensions.Rank != 1 || dimensions.Elements is < 1 or > 15)
            throw new InvalidOperationException("RESHAPE requires one to fifteen dimensions.");
        if ((*(byte*)(source + 23) & 1) != 0)
            throw new NotSupportedException("Derived-type RESHAPE is not implemented.");
        int rank = checked((int)dimensions.Elements);
        long[] extents = new long[rank];
        int[] permutation = Enumerable.Range(0, rank).ToArray();
        long count = 1;
        for (int axis = 0; axis < rank; axis++)
        {
            extents[axis] = IntegerElement(dimensions, axis);
            if (extents[axis] < 0) throw new InvalidOperationException("RESHAPE has a negative extent.");
            count = checked(count * extents[axis]);
        }
        if (order != 0)
        {
            var sequence = new FortranDescriptor(order);
            if (sequence.Rank != 1 || sequence.Elements != rank) throw new InvalidOperationException("Invalid RESHAPE order rank.");
            for (int axis = 0; axis < rank; axis++) permutation[axis] = checked((int)IntegerElement(sequence, axis) - 1);
            if (permutation.Any(axis => axis < 0 || axis >= rank) || permutation.Distinct().Count() != rank)
                throw new InvalidOperationException("RESHAPE order is not a dimension permutation.");
        }
        if (count > input.Elements)
        {
            if (pad == 0) throw new InvalidOperationException("RESHAPE needs PAD for missing source elements.");
            var padding = new FortranDescriptor(pad);
            if (padding.Elements == 0 || padding.Type != input.Type || padding.ElementBytes != input.ElementBytes)
                throw new InvalidOperationException("RESHAPE padding does not conform to the source.");
        }
        NativeMemory.Clear((void*)result, checked((nuint)(24 + rank * 24)));
        *(long*)(result + 8) = input.ElementBytes;
        *(int*)(result + 16) = 20240719;
        *(byte*)(result + 20) = (byte)rank;
        *(sbyte*)(result + 21) = (sbyte)input.Type;
        *(byte*)(result + 22) = 2;
        for (int axis = 0; axis < rank; axis++) FortranAllocation.SetBounds(result, axis, 1, extents[axis]);
        FortranAllocation.Allocate(result, 0, 0, 0, sourceFile, line, 0);
        var output = new FortranDescriptor(result);
        for (long index = 0; index < count; index++)
        {
            long remainder = index;
            nint destination = output.Base;
            foreach (int axis in permutation)
            {
                destination += checked((nint)((remainder % extents[axis]) * output.Stride(axis)));
                remainder /= extents[axis];
            }
            nint address;
            if (index < input.Elements) address = input.Address(index);
            else
            {
                var padding = new FortranDescriptor(pad);
                address = padding.Address((index - input.Elements) % padding.Elements);
            }
            Memory.Copy(destination, address, input.ElementBytes);
        }
    }

    private static long IntegerElement(FortranDescriptor array, long index) => array.ElementBytes switch
    {
        1 => *(sbyte*)array.Address(index), 2 => *(short*)array.Address(index), 4 => *(int*)array.Address(index), 8 => *(long*)array.Address(index),
        _ => throw new NotSupportedException("Fortran shape integer kind is not implemented.")
    };

    [CExport("_FortranAPack")]
    public static void Pack(nint result, nint array, nint mask, nint vector, nint sourceFile, int line)
    {
        var input = new FortranDescriptor(array);
        var selection = new FortranDescriptor(mask);
        if ((*(byte*)(array + 23) & 1) != 0)
            throw new NotSupportedException("Fortran derived-type PACK is not implemented.");
        if (selection.Rank != 0 && (selection.Rank != input.Rank ||
            Enumerable.Range(0, input.Rank).Any(axis => selection.Extent(axis) != input.Extent(axis))))
            throw new InvalidOperationException("PACK mask shape does not match the array.");
        long selected = 0;
        for (long index = 0; index < input.Elements; index++)
            if (selection.Logical(index)) selected++;
        long count = selected;
        if (vector != 0)
        {
            var fill = new FortranDescriptor(vector);
            if (fill.Rank != 1 || fill.Type != input.Type || fill.ElementBytes != input.ElementBytes || fill.Elements < selected)
                throw new InvalidOperationException("PACK vector does not conform to the selected elements.");
            count = fill.Elements;
        }
        NativeMemory.Clear((void*)result, 48);
        *(long*)(result + 8) = input.ElementBytes;
        *(int*)(result + 16) = 20240719;
        *(byte*)(result + 20) = 1;
        *(sbyte*)(result + 21) = (sbyte)input.Type;
        *(byte*)(result + 22) = 2;
        FortranAllocation.SetBounds(result, 0, 1, count);
        FortranAllocation.Allocate(result, 0, 0, 0, sourceFile, line, 0);
        var output = new FortranDescriptor(result);
        long destination = 0;
        for (long index = 0; index < input.Elements; index++)
            if (selection.Logical(index)) Memory.Copy(output.Address(destination++), input.Address(index), input.ElementBytes);
        if (vector != 0)
        {
            var fill = new FortranDescriptor(vector);
            for (; destination < count; destination++)
                Memory.Copy(output.Address(destination), fill.Address(destination), input.ElementBytes);
        }
    }

    [CExport("_FortranASpread")]
    public static void Spread(nint result, nint source, int dimension, long copies, nint sourceFile, int line)
    {
        var input = new FortranDescriptor(source);
        if (input.Rank >= 15 || dimension < 1 || dimension > input.Rank + 1)
            throw new ArgumentOutOfRangeException(nameof(dimension));
        if ((*(byte*)(source + 23) & 1) != 0)
            throw new NotSupportedException("Fortran derived-type SPREAD is not implemented.");
        copies = Math.Max(0, copies);
        NativeMemory.Clear((void*)result, (nuint)(24 + (input.Rank + 1) * 24));
        *(long*)(result + 8) = input.ElementBytes;
        *(int*)(result + 16) = 20240719;
        *(byte*)(result + 20) = (byte)(input.Rank + 1);
        *(sbyte*)(result + 21) = (sbyte)input.Type;
        *(byte*)(result + 22) = 2;
        long prefix = 1;
        for (int axis = 0; axis <= input.Rank; axis++)
        {
            long extent = axis == dimension - 1 ? copies : input.Extent(axis < dimension - 1 ? axis : axis - 1);
            FortranAllocation.SetBounds(result, axis, 1, extent);
            if (axis < dimension - 1) prefix = checked(prefix * extent);
        }
        FortranAllocation.Allocate(result, 0, 0, 0, sourceFile, line, 0);
        var output = new FortranDescriptor(result);
        if (output.Elements == 0) return;
        for (long index = 0; index < output.Elements; index++)
        {
            long sourceIndex = index % prefix + index / checked(prefix * copies) * prefix;
            Memory.Copy(output.Base + checked((nint)(index * output.ElementBytes)), input.Address(sourceIndex), input.ElementBytes);
        }
    }
}