using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

/// <summary>
/// The native kernel packages an image links (ADR-16 Level 3, SV-13), registered by its initializer. Under the JIT the
/// image's P/Invoke stubs name "llvmnet-kernel:&lt;package&gt;:&lt;symbol&gt;", and the resolver loads the package's library
/// for the running host from runtimes/&lt;rid&gt;/native/ beside the image; a kernel called on a host the package has no
/// library for ends the process naming it. A NativeAOT executable links the library of its runtime identifier statically
/// (DirectPInvoke), so the resolver only sees the package's library name on a host it was not published with.
/// </summary>
public static class NativeKernels
{
    private sealed record Package(string Name, string Library, string[] Identifiers, string Directory, bool Available)
    {
        internal nint Handle;
    }

    private static readonly object gate = new();
    private static readonly Dictionary<string, Package> packages = [];
    private static readonly HashSet<Assembly> resolved = [];

    /// <summary>The runtime identifier of this process as packages name them: linux-x64, linux-arm64, win-x64, osx-arm64.</summary>
    public static string RuntimeIdentifier { get; } =
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsFreeBSD() ? "freebsd" : "linux") + "-" +
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>Registers a package of <paramref name="image"/> (from its initializer) with the runtime identifiers it has
    /// libraries for, space-separated.</summary>
    public static void Register(Assembly image, string name, string library, string identifiers)
    {
        lock (gate)
        {
            string directory = RuntimeFeature.IsDynamicCodeCompiled && image.Location.Length != 0 ? Path.GetDirectoryName(image.Location)! : AppContext.BaseDirectory;
            string[] list = identifiers.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // As NativeAOT, the executable's own identifier, whose static library it linked.
            bool available = list.Contains(RuntimeIdentifier) &&
                (!RuntimeFeature.IsDynamicCodeCompiled || File.Exists(Path.Combine(directory, "runtimes", RuntimeIdentifier, "native", FileName(library))));
            packages[name] = new Package(name, library, list, directory, available);
            if (resolved.Add(image)) NativeLibrary.SetDllImportResolver(image, Resolve);
        }
    }

    private static nint Resolve(string library, Assembly image, DllImportSearchPath? path)
    {
        string? symbol = null;
        Package? package;
        lock (gate)
        {
            if (library.StartsWith("llvmnet-kernel:", StringComparison.Ordinal) && library.Split(':') is [_, string name, string entry])
            {
                packages.TryGetValue(name, out package);
                symbol = entry;
            }
            else package = packages.Values.FirstOrDefault(candidate => candidate.Library == library);
            if (package is null) return 0;
            if (package.Handle != 0) return package.Handle;
            string file = Path.Combine(package.Directory, "runtimes", RuntimeIdentifier, "native", FileName(package.Library));
            if (package.Identifiers.Contains(RuntimeIdentifier))
            {
                if (NativeLibrary.TryLoad(file, out nint handle)) return package.Handle = handle;
                NativeExtension.Fail($"native kernel {symbol ?? "function"} of package {package.Name} was called, but its {RuntimeIdentifier} library {file} does not load");
            }
        }
        NativeExtension.Fail($"native kernel {symbol ?? "function"} of package {package.Name} was called, but the package has no library for {RuntimeIdentifier}" +
            $" (it has {string.Join(", ", package.Identifiers)}); the program's own CPU detection should have selected its C path here");
        return 0;
    }

    private static string FileName(string library) =>
        OperatingSystem.IsWindows() ? library + ".dll" : OperatingSystem.IsMacOS() ? "lib" + library + ".dylib" : "lib" + library + ".so";

    /// <summary>Whether the image links the named package and it has a library for the running host (llvmnet_native_kernels,
    /// shared data, optional kernels), as found when the package was registered.</summary>
    public static bool IsAvailable(string name)
    {
        lock (gate)
            return packages.TryGetValue(name, out Package? package) && package.Available;
    }

    /// <summary>
    /// int llvmnet_native_kernels(const char *name) (&lt;llvmnet/cpu.h&gt;): 1 when the program links the native kernel package
    /// of that name and it has a library for the running host (as NativeAOT, the executable's own identifier, whose library
    /// it linked), else 0.
    /// </summary>
    [CExport("llvmnet_native_kernels")]
    public static int Available(nint name) => name != 0 && IsAvailable(Marshal.PtrToStringUTF8(name)!) ? 1 : 0;
}
