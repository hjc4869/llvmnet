using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Runtime;

public static unsafe partial class FortranIO
{
    [CExport("_FortranAioBeginInquireIoLength")]
    public static nint BeginInquireLength(nint source, int line) => New(new Statement("inquire-length", 0));

    [CExport("_FortranAioGetIoLength")]
    public static long GetIoLength(nint cookie) => Get(cookie).IoLength;

    [CExport("_FortranAioBeginInquireFile")]
    public static nint BeginInquireFile(nint file, long length, nint source, int line)
    {
        var state = new Statement("inquire-file", 0);
        state.Options["file"] = Text(file, length).TrimEnd();
        return New(state);
    }

    [CExport("_FortranAioBeginInquireUnit")]
    public static nint BeginInquireUnit(int unit, nint source, int line) => New(new Statement("inquire-unit", unit));

    private static (int Number, Unit? Unit, string? Path) InquiryTarget(Statement state)
    {
        if (state.Operation == "inquire-unit")
        {
            units.TryGetValue(state.Unit, out Unit? unit);
            return (state.Unit, unit, unit?.Path);
        }
        string path = state.Options["file"];
        foreach ((int number, Unit unit) in units)
            if (unit.Path is not null && Path.GetFullPath(unit.Path) == Path.GetFullPath(path))
                return (number, unit, path);
        return (-1, null, path);
    }

    [CExport("_FortranAioInquireLogical")]
    public static int InquireLogical(nint cookie, long keyword, nint destination)
    {
        Statement state = Get(cookie);
        var target = InquiryTarget(state);
        bool value = keyword switch
        {
            14119423 => state.Operation == "inquire-unit" ? target.Unit is not null || state.Unit >= 0 : File.Exists(target.Path) || Directory.Exists(target.Path),
            482188879 => target.Unit is not null,
            17830283 => target.Path is not null && target.Unit?.Scratch != true,
            _ => throw new NotSupportedException($"Fortran logical INQUIRE keyword {keyword} is not implemented.")
        };
        *(byte*)destination = value ? (byte)1 : (byte)0;
        return 1;
    }

    [CExport("_FortranAioInquireInteger64")]
    public static int InquireInteger64(nint cookie, long keyword, nint destination, int kind)
    {
        var target = InquiryTarget(Get(cookie));
        long value = keyword switch
        {
            472724893 => target.Unit is not null ? target.Number : -1,
            779406 => target.Path is not null && File.Exists(target.Path) ? new FileInfo(target.Path).Length : -1,
            28098 => target.Unit is not null ? Stdio.Ftell(target.Unit.File) + 1 : -1,
            _ => throw new NotSupportedException($"Fortran integer INQUIRE keyword {keyword} is not implemented.")
        };
        *(long*)destination = value;
        return 1;
    }

    [CExport("_FortranAioInquireCharacter")]
    public static int InquireCharacter(nint cookie, long keyword, nint destination, long length)
    {
        var target = InquiryTarget(Get(cookie));
        string value = keyword switch
        {
            685780 => target.Path ?? "",
            554774 => target.Unit is null ? "UNDEFINED" : target.Unit.Unformatted ? "UNFORMATTED" : "FORMATTED",
            309868070 => target.Unit is null ? "UNDEFINED" : target.Unit.Direct ? "DIRECT" : target.Unit.Stream ? "STREAM" : "SEQUENTIAL",
            310169457 => target.Unit?.Action ?? "UNDEFINED",
            _ => throw new NotSupportedException($"Fortran character INQUIRE keyword {keyword} is not implemented.")
        };
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        NativeMemory.Fill((void*)destination, checked((nuint)length), (byte)' ');
        bytes.AsSpan(0, checked((int)Math.Min(bytes.Length, length))).CopyTo(new Span<byte>((void*)destination, checked((int)length)));
        return 1;
    }
}