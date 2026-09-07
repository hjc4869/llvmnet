namespace LlvmNet.Runtime;

public static unsafe class Sorting
{
    [CExport("qsort")]
    public static void Qsort(nint memory, long count, long size, nint compare)
    {
        if (count < 2 || size == 0)
            return;
        long[] indices = new long[checked((int)count)];
        for (int index = 0; index < indices.Length; index++)
            indices[index] = index;
        Array.Sort(indices, (left, right) => ((delegate* managed<nint, nint, int>)compare)(memory + checked((nint)(left * size)), memory + checked((nint)(right * size))));
        long bytes = checked(count * size);
        nint temporary = CString.Malloc(bytes);
        if (temporary == 0)
            throw new OutOfMemoryException();
        try
        {
            for (int index = 0; index < indices.Length; index++)
                Memory.Copy(temporary + checked((nint)(index * size)), memory + checked((nint)(indices[index] * size)), size);
            Memory.Copy(memory, temporary, bytes);
        }
        finally { CString.Free(temporary); }
    }

    [CExport("bsearch")]
    public static nint Bsearch(nint key, nint memory, long count, long size, nint compare)
    {
        long low = 0, high = count;
        while (low < high)
        {
            long middle = low + (high - low) / 2;
            nint element = memory + checked((nint)(middle * size));
            int order = ((delegate* managed<nint, nint, int>)compare)(key, element);
            if (order == 0) return element;
            if (order < 0) high = middle;
            else low = middle + 1;
        }
        return 0;
    }
}