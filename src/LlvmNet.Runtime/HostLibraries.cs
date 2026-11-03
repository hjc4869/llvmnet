using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

/// <summary>
/// A host library that a generated native extension stands for (ADR-15, docs/user/extensions.md), found at run time under
/// its names for the running operating system: in the directory that LLVMNET_EXT_&lt;NAME&gt;_DIR names, then beside the
/// image, then on the system loader's default path. The libraries listed as preloads are loaded first, so that a library
/// that depends on them (ANGLE's libEGL on libGLESv2) finds them already loaded from the same directory. Loading happens
/// once, on first use; a failure is remembered with the candidates tried.
/// </summary>
public sealed class HostLibrary
{
    private readonly object gate = new();
    private readonly string[] names;
    private readonly HostLibrary[] preload;
    private bool attempted;
    private nint handle;
    private string? path;
    private string? problem;

    /// <param name="extension">The extension's name; it names the LLVMNET_EXT_&lt;NAME&gt;_DIR variable.</param>
    /// <param name="name">The library's name in messages (EGL).</param>
    /// <param name="linux">The file names on Linux, tried in order (libEGL.so.1).</param>
    /// <param name="windows">The file names on Windows (libEGL.dll).</param>
    /// <param name="macOS">The file names on macOS (libEGL.dylib).</param>
    /// <param name="preload">Libraries to load first, whose failure does not matter here.</param>
    public HostLibrary(string extension, string name, string[] linux, string[] windows, string[] macOS, params HostLibrary[] preload)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        ArgumentException.ThrowIfNullOrEmpty(name);
        Extension = extension;
        Name = name;
        names = OperatingSystem.IsWindows() ? windows : OperatingSystem.IsMacOS() ? macOS : linux;
        this.preload = preload;
    }

    public string Extension { get; }

    public string Name { get; }

    /// <summary>The environment variable that names a directory searched first: LLVMNET_EXT_GLES_DIR for gles.</summary>
    public string DirectoryVariable => $"LLVMNET_EXT_{Extension.ToUpperInvariant().Replace('-', '_')}_DIR";

    /// <summary>The library's handle, loading it on first use, or 0 when it is not available (<see cref="Problem"/>).</summary>
    public nint Handle
    {
        get
        {
            if (Volatile.Read(ref attempted)) return handle;
            foreach (HostLibrary library in preload) _ = library.Handle;
            lock (gate)
            {
                if (!attempted)
                {
                    Load();
                    Volatile.Write(ref attempted, true);
                }
                return handle;
            }
        }
    }

    /// <summary>The path or name the library was loaded from, or null.</summary>
    public string? LoadedFrom => Handle != 0 ? path : null;

    /// <summary>Why the library is not available (the candidates tried and the loader's last message), or null.</summary>
    public string? Problem => Handle == 0 ? problem : null;

    /// <summary>The address of an exported function, or 0 when the library is not available or does not export it.</summary>
    public nint Export(string symbol) => Handle is var library and not 0 && NativeLibrary.TryGetExport(library, symbol, out nint address) ? address : 0;

    private void Load()
    {
        List<string> candidates = [];
        string? directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrEmpty(directory)) candidates.AddRange(names.Select(name => System.IO.Path.Combine(directory, name)));
        string beside = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(beside)) candidates.AddRange(names.Select(name => System.IO.Path.Combine(beside, name)));
        candidates.AddRange(names);
        if (names.Length == 0)
        {
            problem = $"the host library {Name} of the native extension {Extension} does not exist on this operating system";
            return;
        }
        string? message = null;
        foreach (string candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            // A rooted candidate that does not exist is skipped quietly; the loader's message for one that exists, or
            // for a name searched on the default path, says why it did not load (a missing dependency, another CPU).
            if (System.IO.Path.IsPathRooted(candidate) && !File.Exists(candidate)) continue;
            try
            {
                handle = NativeLibrary.Load(candidate);
                path = candidate;
                return;
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
            {
                message = LoaderMessage(error.Message);
            }
        }
        problem = $"the host library {Name} of the native extension {Extension} is not available (tried {string.Join(", ", candidates.Distinct(StringComparer.Ordinal))}" +
            (directory is null or "" ? $"; {DirectoryVariable} names another directory" : "") + ")" + (message is null ? "" : ": " + message);
    }

    /// <summary>
    /// The system loader's own words from .NET's message: the lines after its first on Linux and macOS (dlerror's text),
    /// what follows "dependencies: " on Windows.
    /// </summary>
    internal static string LoaderMessage(string message)
    {
        string text = message.Trim();
        int newline = text.IndexOf('\n');
        if (newline >= 0) return string.Join("; ", text[(newline + 1)..].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        int colon = text.IndexOf("dependencies: ", StringComparison.Ordinal);
        return colon >= 0 ? text[(colon + "dependencies: ".Length)..] : text;
    }
}

/// <summary>
/// How the stubs of a generated native extension reach their host functions: a function is the export of its library,
/// or what the API's own lookup function returns for it (eglGetProcAddress), as an application would find it natively.
/// A stub binds once, on its first call, and keeps the address.
/// </summary>
public sealed class HostEntryPoints
{
    private readonly Func<string, nint>? lookup;

    /// <param name="extension">The extension's name, for messages.</param>
    /// <param name="libraries">Its host libraries, in the order availability loads them.</param>
    /// <param name="lookup">The API's lookup function for functions the libraries do not export, or null.</param>
    public HostEntryPoints(string extension, HostLibrary[] libraries, Func<string, nint>? lookup = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        Extension = extension;
        Libraries = [.. libraries];
        this.lookup = lookup;
    }

    public string Extension { get; }

    public IReadOnlyList<HostLibrary> Libraries { get; }

    /// <summary>The host function: the library's export, else the lookup function's answer; 0 when the host has none.</summary>
    public nint Find(HostLibrary library, string name)
    {
        if (library.Handle == 0) return 0;
        nint address = library.Export(name);
        return address != 0 || lookup is null ? address : lookup(name);
    }

    /// <summary>Binds a stub to its host function, or ends the process with one message when the host has none.</summary>
    public nint Bind(ref nint slot, HostLibrary library, string name)
    {
        nint address = Find(library, name);
        if (address == 0)
            NativeExtension.Fail($"{name}: " + (library.Problem ?? $"the host library {library.Name} of the native extension {Extension} ({library.LoadedFrom}) does not provide {name}"));
        return slot = address;
    }

    /// <summary>Binds a stub that has a default implementation: 0 when the host has no such function.</summary>
    public nint TryBind(ref nint slot, HostLibrary library, string name)
    {
        nint address = Find(library, name);
        if (address != 0) slot = address;
        return address;
    }

    /// <summary>
    /// Why the extension cannot be used on this host, or null when every host library loads and provides every required
    /// function; loads the libraries.
    /// </summary>
    public string? Unavailable(params (HostLibrary Library, string Name)[] required)
    {
        foreach (HostLibrary library in Libraries)
            if (library.Problem is string problem) return problem;
        foreach ((HostLibrary library, string name) in required)
            if (Find(library, name) == 0)
                return $"the host library {library.Name} of the native extension {Extension} ({library.LoadedFrom}) does not provide {name}";
        return null;
    }

    /// <summary>One line per host library: where it was loaded from, or why it was not.</summary>
    public string Report() => string.Join("\n", Libraries.Select(library => library.LoadedFrom is string path ? $"{library.Name}: {path}" : $"{library.Name}: {library.Problem}"));
}

/// <summary>
/// Reverse thunks of generated native extensions: a fixed pool of native-callable entry points per callback type, each
/// calling the portable function stored in its slot. The same portable function always gets the same entry point, so a
/// callback registered twice or compared by address behaves as natively; slots are never released.
/// </summary>
public static class NativeCallbacks
{
    private static readonly object gate = new();

    /// <summary>
    /// The slot of <paramref name="targets"/> that holds <paramref name="target"/>, storing it in a free slot first when
    /// needed; ends the process with a message when every slot holds another function.
    /// </summary>
    public static int Slot(nint[] targets, nint target, string extension, string type)
    {
        ArgumentNullException.ThrowIfNull(targets);
        lock (gate)
        {
            for (int index = 0; index < targets.Length; index++)
                if (targets[index] == target) return index;
            for (int index = 0; index < targets.Length; index++)
                if (targets[index] == 0)
                {
                    Volatile.Write(ref targets[index], target);
                    return index;
                }
        }
        NativeExtension.Fail($"the native extension {extension} has entry points for {targets.Length} distinct {type} callbacks, and the program passed another");
        return -1;
    }
}
