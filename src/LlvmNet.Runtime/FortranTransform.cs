using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class FortranTransform
{
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