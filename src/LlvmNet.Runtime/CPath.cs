using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class CPath
{
    private static readonly nint dot = Marshal.StringToCoTaskMemUTF8(".");

    [CExport("basename")]
    public static nint Basename(nint path)
    {
        if (path == 0 || *(byte*)path == 0) return dot;
        byte* text = (byte*)path;
        long end = CString.Strlen(path) - 1;
        while (end > 0 && text[end] == '/') end--;
        text[end + 1] = 0;
        if (end == 0 && text[0] == '/') return path;
        while (end >= 0 && text[end] != '/') end--;
        return path + checked((nint)(end + 1));
    }

    [CExport("dirname")]
    public static nint Dirname(nint path)
    {
        if (path == 0 || *(byte*)path == 0) return dot;
        byte* text = (byte*)path;
        long end = CString.Strlen(path) - 1;
        while (end > 0 && text[end] == '/') end--;
        while (end >= 0 && text[end] != '/') end--;
        if (end < 0) return dot;
        while (end > 0 && text[end - 1] == '/') end--;
        text[end == 0 ? 1 : end] = 0;
        return path;
    }
}