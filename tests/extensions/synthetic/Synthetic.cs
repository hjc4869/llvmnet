// The synthetic native extension (NX-03): stubs with llvmnet's CIL signatures for the C prototypes of
// include/llvmnet-synthetic.h, each calling the native test library through an unmanaged function pointer with the exact
// C types, bound on first use; and the registration class, which loads the library under its name for the running
// operating system, from LLVMNET_EXT_SYNTHETIC_DIR when it is set.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LlvmNet.Runtime;

namespace LlvmNet.Ext.Synthetic;

public sealed unsafe class SyntheticExtension() : NativeExtension("synthetic", "libllvmnet-synthetic.so.1", "libllvmnet-synthetic.so")
{
    private static readonly object gate = new();
    private static nint library;

    private static readonly Dictionary<string, nint> stubs = new(StringComparer.Ordinal)
    {
        ["synthetic_version"] = (nint)(delegate*<nint>)&Api.synthetic_version,
        ["synthetic_add"] = (nint)(delegate*<int, int, int>)&Api.synthetic_add,
        ["synthetic_mix"] = (nint)(delegate*<int, int, int, int>)&Api.synthetic_mix,
        ["synthetic_low"] = (nint)(delegate*<int, int>)&Api.synthetic_low,
        ["synthetic_wide"] = (nint)(delegate*<long, long, long>)&Api.synthetic_wide,
        ["synthetic_scale"] = (nint)(delegate*<double, float, double>)&Api.synthetic_scale,
        ["synthetic_format"] = (nint)(delegate*<nint, long, nint, int, long>)&Api.synthetic_format,
    };

    private static string[] Names => OperatingSystem.IsWindows() ? ["llvmnet-synthetic.dll"]
        : OperatingSystem.IsMacOS() ? ["libllvmnet-synthetic.dylib"] : ["libllvmnet-synthetic.so.1"];

    /// <summary>The host library's handle, or 0 with the reason when it cannot be loaded.</summary>
    private static nint Load(out string? problem)
    {
        problem = null;
        lock (gate)
        {
            if (library != 0) return library;
            string? directory = Environment.GetEnvironmentVariable("LLVMNET_EXT_SYNTHETIC_DIR");
            string[] candidates = [.. Names.Select(name => directory is null ? name : Path.Combine(directory, name))];
            foreach (string candidate in candidates)
                if (NativeLibrary.TryLoad(candidate, out nint handle)) return library = handle;
            problem = $"synthetic: cannot load the host library (tried {string.Join(", ", candidates)})";
            return 0;
        }
    }

    public override string? Open(string library) => Load(out string? problem) == 0 ? problem : null;

    public override nint Resolve(string library, string symbol) =>
        stubs.TryGetValue(symbol, out nint stub) && Load(out _) is nint handle and not 0 && NativeLibrary.TryGetExport(handle, symbol, out _) ? stub : 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static nint Bind(ref nint slot, string name)
    {
        nint handle = Load(out string? problem);
        if (handle == 0) throw new DllNotFoundException(problem);
        return slot = NativeLibrary.GetExport(handle, name);
    }
}

public static unsafe class Api
{
    private static nint version, add, mix, low, wide, scale, format;

    [CExport("synthetic_version")]
    public static nint synthetic_version()
    {
        nint function = version != 0 ? version : SyntheticExtension.Bind(ref version, "synthetic_version");
        return ((delegate* unmanaged<nint>)function)();
    }

    [CExport("synthetic_add")]
    public static int synthetic_add(int left, int right)
    {
        nint function = add != 0 ? add : SyntheticExtension.Bind(ref add, "synthetic_add");
        return ((delegate* unmanaged<int, int, int>)function)(left, right);
    }

    [CExport("synthetic_mix")]
    public static int synthetic_mix(int value, int half, int word)
    {
        nint function = mix != 0 ? mix : SyntheticExtension.Bind(ref mix, "synthetic_mix");
        return ((delegate* unmanaged<byte, short, uint, int>)function)((byte)value, (short)half, (uint)word);
    }

    [CExport("synthetic_low")]
    public static int synthetic_low(int value)
    {
        nint function = low != 0 ? low : SyntheticExtension.Bind(ref low, "synthetic_low");
        return ((delegate* unmanaged<uint, byte>)function)((uint)value);
    }

    [CExport("synthetic_wide")]
    public static long synthetic_wide(long value, long shift)
    {
        nint function = wide != 0 ? wide : SyntheticExtension.Bind(ref wide, "synthetic_wide");
        return ((delegate* unmanaged<long, ulong, long>)function)(value, (ulong)shift);
    }

    [CExport("synthetic_scale")]
    public static double synthetic_scale(double value, float factor)
    {
        nint function = scale != 0 ? scale : SyntheticExtension.Bind(ref scale, "synthetic_scale");
        return ((delegate* unmanaged<double, float, double>)function)(value, factor);
    }

    [CExport("synthetic_format")]
    public static long synthetic_format(nint buffer, long size, nint name, int value)
    {
        nint function = format != 0 ? format : SyntheticExtension.Bind(ref format, "synthetic_format");
        return (long)((delegate* unmanaged<nint, nuint, nint, int, nuint>)function)(buffer, (nuint)size, name, value);
    }
}
