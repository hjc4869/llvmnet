using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class CString
{
    [ThreadStatic] private static nint tokenState;

    public static string Text(nint pointer) => pointer == 0 ? "(null)" : Marshal.PtrToStringUTF8(pointer) ?? "";

    [CExport("malloc")]
    public static nint Malloc(long size)
    {
        if (AbiContract.Portable)
        {
            nint result = PortableHeap.Allocate(size);
            if (result == 0) ProcessRuntime.Error(12);
            return result;
        }
        if (size < 0)
        {
            ProcessRuntime.Error(12);
            return 0;
        }
        try { return (nint)NativeMemory.Alloc((nuint)Math.Max(1, size)); }
        catch (OutOfMemoryException) { ProcessRuntime.Error(12); return 0; }
    }

    [CExport("calloc")]
    public static nint Calloc(long count, long size)
    {
        if (count < 0 || size < 0 || (count != 0 && size > long.MaxValue / count))
        {
            ProcessRuntime.Error(12);
            return 0;
        }
        nint pointer = Malloc(count * size);
        if (pointer != 0)
            NativeMemory.Clear((void*)pointer, (nuint)(count * size));
        return pointer;
    }

    [CExport("realloc")]
    public static nint Realloc(nint pointer, long size)
    {
        if (AbiContract.Portable)
        {
            nint result = PortableHeap.Reallocate(pointer, size);
            if (result == 0 && size != 0) ProcessRuntime.Error(12);
            return result;
        }
        if (size < 0)
        {
            ProcessRuntime.Error(12);
            return 0;
        }
        if (size == 0)
        {
            Free(pointer);
            return 0;
        }
        try { return (nint)NativeMemory.Realloc((void*)pointer, (nuint)size); }
        catch (OutOfMemoryException) { ProcessRuntime.Error(12); return 0; }
    }

    [CExport("free")]
    public static void Free(nint pointer)
    {
        if (AbiContract.Portable) PortableHeap.Free(pointer);
        else NativeMemory.Free((void*)pointer);
    }

    [CExport("posix_memalign")]
    public static int PosixMemalign(nint result, long alignment, long size)
    {
        if (alignment < 8 || (alignment & (alignment - 1)) != 0 || size < 0)
            return 22;
        if (AbiContract.Portable)
        {
            nint pointer = PortableHeap.Allocate(size, alignment);
            if (pointer == 0) return 12;
            *(nint*)result = pointer;
            return 0;
        }
        try { *(nint*)result = Memory.Allocate(size, checked((int)alignment)); return 0; }
        catch (OutOfMemoryException) { return 12; }
    }

    [CExport("aligned_alloc")]
    public static nint AlignedAlloc(long alignment, long size)
    {
        nint result;
        if (alignment <= 0 || size < 0 || size % alignment != 0) { ProcessRuntime.Error(22); return 0; }
        int error = PosixMemalign((nint)(&result), alignment, size);
        if (error != 0) { ProcessRuntime.Error(error); return 0; }
        return result;
    }

    [CExport("memcpy"), CExport("memmove")]
    public static nint Memcpy(nint destination, nint source, long size) => Memory.Copy(destination, source, size);

    [CExport("memset")]
    public static nint Memset(nint destination, int value, long size) => Memory.Set(destination, value, size);

    [CExport("memcmp"), CExport("bcmp")]
    public static int Memcmp(nint left, nint right, long size)
    {
        for (long index = 0; index < size; index++)
        {
            int difference = ((byte*)left)[index] - ((byte*)right)[index];
            if (difference != 0)
                return difference;
        }
        return 0;
    }

    [CExport("memchr")]
    public static nint Memchr(nint memory, int value, long size)
    {
        for (long index = 0; index < size; index++)
            if (((byte*)memory)[index] == (byte)value)
                return memory + (nint)index;
        return 0;
    }

    [CExport("strlen")]
    public static long Strlen(nint text)
    {
        long length = 0;
        while (((byte*)text)[length] != 0)
            length++;
        return length;
    }

    [CExport("strnlen")]
    public static long Strnlen(nint text, long maximum)
    {
        long length = 0;
        while (length < maximum && ((byte*)text)[length] != 0)
            length++;
        return length;
    }

    [CExport("strcmp")]
    public static int Strcmp(nint left, nint right) => Strncmp(left, right, long.MaxValue);

    [CExport("strncmp")]
    public static int Strncmp(nint left, nint right, long count)
    {
        for (long index = 0; index < count; index++)
        {
            int difference = ((byte*)left)[index] - ((byte*)right)[index];
            if (difference != 0 || ((byte*)left)[index] == 0)
                return difference;
        }
        return 0;
    }

    [CExport("strcasecmp")]
    public static int Strcasecmp(nint left, nint right) => Strncasecmp(left, right, long.MaxValue);

    [CExport("strncasecmp")]
    public static int Strncasecmp(nint left, nint right, long count)
    {
        for (long index = 0; index < count; index++)
        {
            int difference = Lower(((byte*)left)[index]) - Lower(((byte*)right)[index]);
            if (difference != 0 || ((byte*)left)[index] == 0)
                return difference;
        }
        return 0;
    }

    [CExport("tolower")]
    public static int Lower(int value) => value is >= 'A' and <= 'Z' ? value + 32 : value;
    [CExport("toupper")]
    public static int Upper(int value) => value is >= 'a' and <= 'z' ? value - 32 : value;

    [CExport("strcpy")]
    public static nint Strcpy(nint destination, nint source) => Memcpy(destination, source, Strlen(source) + 1);

    [CExport("strncpy")]
    public static nint Strncpy(nint destination, nint source, long count)
    {
        long length = Strnlen(source, count);
        Memcpy(destination, source, length);
        Memset(destination + (nint)length, 0, count - length);
        return destination;
    }

    [CExport("strcat")]
    public static nint Strcat(nint destination, nint source)
    {
        Strcpy(destination + (nint)Strlen(destination), source);
        return destination;
    }

    [CExport("strchr")]
    public static nint Strchr(nint text, int value)
    {
        do
        {
            if (*(byte*)text == (byte)value)
                return text;
        } while (*(byte*)text++ != 0);
        return 0;
    }

    [CExport("strrchr")]
    public static nint Strrchr(nint text, int value)
    {
        nint result = 0;
        do
        {
            if (*(byte*)text == (byte)value)
                result = text;
        } while (*(byte*)text++ != 0);
        return result;
    }

    [CExport("strstr")]
    public static nint Strstr(nint text, nint needle)
    {
        long length = Strlen(needle);
        if (length == 0)
            return text;
        long maximum = Strlen(text) - length;
        for (long index = 0; index <= maximum; index++)
            if (Memcmp(text + (nint)index, needle, length) == 0)
                return text + (nint)index;
        return 0;
    }

    [CExport("strdup")]
    public static nint Strdup(nint text)
    {
        long size = Strlen(text) + 1;
        nint result = Malloc(size);
        return result == 0 ? 0 : Memcpy(result, text, size);
    }

    [CExport("strspn")]
    public static long Strspn(nint text, nint accepted)
    {
        long length = 0;
        while (((byte*)text)[length] != 0 && Strchr(accepted, ((byte*)text)[length]) != 0)
            length++;
        return length;
    }

    [CExport("strcspn")]
    public static long Strcspn(nint text, nint rejected)
    {
        long length = 0;
        while (((byte*)text)[length] != 0 && Strchr(rejected, ((byte*)text)[length]) == 0)
            length++;
        return length;
    }

    [CExport("strpbrk")]
    public static nint Strpbrk(nint text, nint accepted)
    {
        nint result = text + (nint)Strcspn(text, accepted);
        return *(byte*)result == 0 ? 0 : result;
    }

    [CExport("strtok")]
    public static nint Strtok(nint text, nint delimiters) => Tokenize(text, delimiters, ref tokenState);

    [CExport("strtok_r")]
    public static nint StrtokR(nint text, nint delimiters, nint saved) => Tokenize(text, delimiters, ref *(nint*)saved);

    private static nint Tokenize(nint text, nint delimiters, ref nint saved)
    {
        if (text == 0) text = saved;
        if (text == 0) return 0;
        text += checked((nint)Strspn(text, delimiters));
        if (*(byte*)text == 0) { saved = text; return 0; }
        nint end = text + checked((nint)Strcspn(text, delimiters));
        saved = end;
        if (*(byte*)end != 0) { *(byte*)end = 0; saved++; }
        return text;
    }

    [CExport("wcslen")]
    public static long Wcslen(nint text)
    {
        long length = 0;
        while (((uint*)text)[length] != 0)
            length++;
        return length;
    }
}