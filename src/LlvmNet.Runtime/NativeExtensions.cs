using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Runtime;

/// <summary>
/// A native extension of the portable ABI (ADR-15, docs/plan/11-native-extensions.md): a managed assembly that stands for
/// host libraries a portable program calls, such as EGL, with a stub per entry point that calls the host's implementation
/// through an unmanaged function pointer. An image linked with <c>--extension</c> registers it at start
/// (<see cref="NativeExtensions.Register"/>), and dlopen, dlsym and dladdr then serve its libraries
/// (<c>HostSyscalls.DlOpen</c>). The driver finds the subclass, which must be public with a public parameterless
/// constructor, in the assembly the extension package installs.
/// </summary>
public abstract class NativeExtension
{
    /// <param name="name">The extension's name, as its package under the sysroot's ext/ directory is named.</param>
    /// <param name="libraries">The library names dlopen serves, as programs spell them (libEGL.so.1, libEGL.so); dlopen
    /// matches the last component of the path it is given.</param>
    protected NativeExtension(string name, params string[] libraries)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (libraries.Length == 0 || libraries.Any(string.IsNullOrEmpty))
            throw new ArgumentException($"The native extension {name} must serve at least one library, each with a name.", nameof(libraries));
        Name = name;
        Libraries = [.. libraries];
    }

    /// <summary>An extension that serves no dlopen name (<see cref="SystemModeExtension"/>).</summary>
    private protected NativeExtension(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Libraries = [];
    }

    public string Name { get; }

    public IReadOnlyList<string> Libraries { get; }

    /// <summary>
    /// Makes the host library behind <paramref name="library"/>, one of <see cref="Libraries"/>, available for dlopen,
    /// loading it if needed. Returns null when it is available, else the reason dlerror reports (the names tried). The
    /// default serves libraries whose stubs need no host library.
    /// </summary>
    public virtual string? Open(string library) => null;

    /// <summary>
    /// The managed function pointer of the stub for <paramref name="symbol"/> in <paramref name="library"/>, or 0 when the
    /// extension has no stub for it or the host library does not provide the function, so that lookups fail where they
    /// would natively. Translated code calls the pointer with the CIL signature of the C prototype it was cast to.
    /// </summary>
    public abstract nint Resolve(string library, string symbol);

    /// <summary>
    /// Why the extension cannot be used on this host (a host library that does not load, a required function it does not
    /// provide), or null when it can; llvmnet_ext_available reports it, and it may load the host libraries. The default
    /// is for extensions whose stubs need no host library.
    /// </summary>
    public virtual string? Unavailable() => null;

    /// <summary>What the extension loaded (one line per host library with its path) or why it could not, for
    /// llvmnet_ext_library.</summary>
    public virtual string Report() => Unavailable() ?? $"{Name}: no host library";

    /// <summary>
    /// Ends the process with one line on standard error, "llvmnet: " and the message, and exit status 127, as a native
    /// loader does when a library or a lazily bound function is missing; extension stubs call it when their host function
    /// is not available and they have no default implementation.
    /// </summary>
    [DoesNotReturn]
    public static void Fail(string message)
    {
        Console.Out.Flush();
        Console.Error.WriteLine("llvmnet: " + message);
        Environment.Exit(127);
        throw new InvalidOperationException(message);
    }
}

/// <summary>
/// A native extension linked into a system-mode image (--extension in system mode), where the program calls the host
/// libraries through generated bridges and the system loader has loaded them before the program started; it serves no
/// dlopen name and is registered so that llvmnet_ext_available and llvmnet_ext_library answer as in portable mode.
/// </summary>
public sealed class SystemModeExtension(string name, string libraries) : NativeExtension(name)
{
    public override nint Resolve(string library, string symbol) => 0;

    public override string Report() => $"{Name}: linked in system mode with {libraries}";
}

/// <summary>The registry of the native extensions that serve dlopen in this process.</summary>
public static class NativeExtensions
{
    private static readonly object gate = new();
    private static readonly List<NativeExtension> registered = [];

    /// <summary>
    /// Registers an extension; emitted image initializers call this before any translated code runs. A second extension of
    /// the same name is ignored (a program and a library linked with the same extension), and a library that another
    /// extension already serves is an error.
    /// </summary>
    public static void Register(NativeExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        lock (gate)
        {
            if (registered.Any(other => other.Name == extension.Name)) return;
            foreach (string library in extension.Libraries)
                if (registered.FirstOrDefault(other => other.Libraries.Contains(library)) is NativeExtension other)
                    throw new InvalidOperationException($"The native extensions {other.Name} and {extension.Name} both serve {library}.");
            registered.Add(extension);
        }
    }

    /// <summary>Registers a system-mode image's extension (<see cref="SystemModeExtension"/>); emitted image initializers
    /// call this with the extension's name and its Linux libraries.</summary>
    public static void RegisterSystem(string name, string libraries) => Register(new SystemModeExtension(name, libraries));

    /// <summary>The registered extension that serves a library name, or null.</summary>
    internal static NativeExtension? Find(string library)
    {
        lock (gate) return registered.FirstOrDefault(extension => extension.Libraries.Contains(library));
    }

    /// <summary>The registered extension of a name, or null.</summary>
    internal static NativeExtension? Named(string name)
    {
        lock (gate) return registered.FirstOrDefault(extension => extension.Name == name);
    }

    private static readonly Dictionary<string, nint> reports = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Extension, string Name), nint> wrappers = [];

    /// <summary>
    /// void __llvmnet_ext_register(const char *extension, const char *name, void *function): the C wrapper of an entry point
    /// that passes structures by value (NX-07), which the extension's lib/wrappers.o defines in the program and registers
    /// from a constructor, so that dlsym and the API's lookup function can hand it out.
    /// </summary>
    [CExport("__llvmnet_ext_register")]
    public static void RegisterWrapper(nint extension, nint name, nint function)
    {
        lock (wrappers) wrappers[(Marshal.PtrToStringUTF8(extension)!, Marshal.PtrToStringUTF8(name)!)] = function;
    }

    /// <summary>The wrapper the program registered for an extension's entry point, or 0.</summary>
    public static nint Wrapper(string extension, string name)
    {
        lock (wrappers) return wrappers.TryGetValue((extension, name), out nint function) ? function : 0;
    }

    /// <summary>
    /// int llvmnet_ext_available(const char *name) (&lt;llvmnet/ext.h&gt;): 1 when the program is linked with the native
    /// extension of that name and the extension can be used on this host, which loads its host libraries; else 0.
    /// </summary>
    [CExport("llvmnet_ext_available")]
    public static int Available(nint name)
    {
        if (name == 0 || Named(Marshal.PtrToStringUTF8(name)!) is not NativeExtension extension) return 0;
        try { return extension.Unavailable() is null ? 1 : 0; }
        catch (Exception error) when (error is not OutOfMemoryException) { return 0; }
    }

    /// <summary>
    /// const char *llvmnet_ext_library(const char *name) (&lt;llvmnet/ext.h&gt;): what the named extension loaded or why it
    /// could not, one line per host library, in a string that stays valid until the process ends; NULL when the program is
    /// not linked with the extension.
    /// </summary>
    [CExport("llvmnet_ext_library")]
    public static unsafe nint Library(nint name)
    {
        if (name == 0 || Named(Marshal.PtrToStringUTF8(name)!) is not NativeExtension extension) return 0;
        string report;
        try { report = extension.Report(); }
        catch (Exception error) when (error is not OutOfMemoryException) { report = $"{extension.Name}: {error.Message}"; }
        lock (reports)
        {
            if (reports.TryGetValue(report, out nint existing)) return existing;
            byte[] bytes = Encoding.UTF8.GetBytes(report + "\0");
            nint copy = (nint)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>((void*)copy, bytes.Length));
            reports.Add(report, copy);
            return copy;
        }
    }
}
