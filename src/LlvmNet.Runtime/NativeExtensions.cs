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

    /// <summary>The registered extension that serves a library name, or null.</summary>
    internal static NativeExtension? Find(string library)
    {
        lock (gate) return registered.FirstOrDefault(extension => extension.Libraries.Contains(library));
    }
}
