using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class CType
{
    private static readonly nint classification;
    private static readonly nint lowercase;
    private static readonly nint uppercase;

    static CType()
    {
        ushort* classes = (ushort*)NativeMemory.AllocZeroed(384, 2);
        int* lower = (int*)NativeMemory.AllocZeroed(384, 4);
        int* upper = (int*)NativeMemory.AllocZeroed(384, 4);
        for (int index = -128; index < 256; index++)
        {
            int value = index < -1 ? index + 256 : index;
            lower[index + 128] = CString.Lower(value);
            upper[index + 128] = CString.Upper(value);
            ushort bits = 0;
            bool isUpper = value is >= 'A' and <= 'Z';
            bool isLower = value is >= 'a' and <= 'z';
            bool digit = value is >= '0' and <= '9';
            bool alpha = isUpper || isLower;
            bool graph = value is >= 33 and <= 126;
            if (isUpper) bits |= 0x100;
            if (isLower) bits |= 0x200;
            if (alpha) bits |= 0x400;
            if (digit) bits |= 0x800;
            if (digit || value is >= 'a' and <= 'f' or >= 'A' and <= 'F') bits |= 0x1000;
            if (value == 32 || value is >= 9 and <= 13) bits |= 0x2000;
            if (value is >= 32 and <= 126) bits |= 0x4000;
            if (graph) bits |= 0x8000;
            if (value is 9 or 32) bits |= 1;
            if (value is >= 0 and <= 31 or 127) bits |= 2;
            if (graph && !alpha && !digit) bits |= 4;
            if (alpha || digit) bits |= 8;
            classes[index + 128] = bits;
        }
        classification = Indirect((nint)(classes + 128));
        lowercase = Indirect((nint)(lower + 128));
        uppercase = Indirect((nint)(upper + 128));
    }

    private static nint Indirect(nint pointer)
    {
        nint slot = (nint)NativeMemory.Alloc((nuint)IntPtr.Size);
        *(nint*)slot = pointer;
        return slot;
    }
    [CExport("__ctype_b_loc")]
    public static nint Classes() => classification;
    [CExport("__ctype_tolower_loc")]
    public static nint Lowercase() => lowercase;
    [CExport("__ctype_toupper_loc")]
    public static nint Uppercase() => uppercase;
    private static int Check(int value, int mask) => value is >= -128 and <= 255 ? ((ushort*)*(nint*)classification)[value] & mask : 0;
    [CExport("isalpha")] public static int Isalpha(int value) => Check(value, 0x400);
    [CExport("isalnum")] public static int Isalnum(int value) => Check(value, 8);
    [CExport("isdigit")] public static int Isdigit(int value) => Check(value, 0x800);
    [CExport("isxdigit")] public static int Isxdigit(int value) => Check(value, 0x1000);
    [CExport("isupper")] public static int Isupper(int value) => Check(value, 0x100);
    [CExport("islower")] public static int Islower(int value) => Check(value, 0x200);
    [CExport("isspace")] public static int Isspace(int value) => Check(value, 0x2000);
    [CExport("isprint")] public static int Isprint(int value) => Check(value, 0x4000);
    [CExport("isgraph")] public static int Isgraph(int value) => Check(value, 0x8000);
    [CExport("ispunct")] public static int Ispunct(int value) => Check(value, 4);
    [CExport("iscntrl")] public static int Iscntrl(int value) => Check(value, 2);
    [CExport("isblank")] public static int Isblank(int value) => Check(value, 1);
}