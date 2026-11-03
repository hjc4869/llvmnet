using System.Runtime.InteropServices;
using System.Text;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>
/// Dynamic loading for portable programs (ADR-15): musl's dlopen, dlsym, dlclose, dlerror and dladdr
/// (ports/musl/src/ldso/llvmnet) over the native extensions the image registered (<see cref="NativeExtensions"/>). A handle
/// stands for one library of one extension and points to the library's name; libraries stay loaded, as in musl. A failed
/// call leaves its message for __llvmnet_dlerror on the calling thread, which musl's port copies into dlerror's buffer.
/// </summary>
public static unsafe partial class HostSyscalls
{
    private sealed class LoadedLibrary(NativeExtension extension, string name, nint handle)
    {
        internal readonly NativeExtension Extension = extension;
        internal readonly string Name = name;
        internal readonly nint Handle = handle;
        internal bool Global;
    }

    /// <summary>A stub dlsym handed out, for dladdr; the name is allocated when dladdr first describes it.</summary>
    private sealed class ResolvedSymbol(LoadedLibrary library, string name)
    {
        internal readonly LoadedLibrary Library = library;
        internal readonly string Name = name;
        internal nint NativeName;
    }

    private static readonly object loadingGate = new();
    private static readonly List<LoadedLibrary> loadedLibraries = [];
    private static readonly Dictionary<nint, ResolvedSymbol> resolvedSymbols = [];
    [ThreadStatic] private static string? loadingError;

    /// <summary>
    /// dlopen: a handle for the library an extension serves under the last component of <paramref name="file"/>, the same
    /// one on every call, or 0. A name no extension serves fails with musl's message for static programs plus the name; a
    /// null name fails too, because a portable program's own symbols cannot be looked up. Every RTLD_* flag is accepted:
    /// RTLD_GLOBAL adds the library to what RTLD_DEFAULT searches, RTLD_NOLOAD opens nothing new.
    /// </summary>
    [CExport("__llvmnet_dlopen")]
    public static nint DlOpen(nint file, int flags)
    {
        if (file == 0) return LoadingError("Dynamic loading not supported: a portable program cannot look up its own symbols");
        string path = Marshal.PtrToStringUTF8(file)!;
        string name = path[(path.LastIndexOf('/') + 1)..];
        if (NativeExtensions.Find(name) is not NativeExtension extension) return LoadingError("Dynamic loading not supported: " + path);
        lock (loadingGate)
        {
            LoadedLibrary? library = loadedLibraries.Find(loaded => loaded.Extension == extension && loaded.Name == name);
            if (library is null)
            {
                if ((flags & RTLD_NOLOAD) != 0) return LoadingError($"Library {path} is not already loaded");
                string? problem;
                try { problem = extension.Open(name); }
                catch (Exception error) { problem = $"{extension.Name}: {error.Message}"; }
                if (problem is not null) return LoadingError(problem);
                library = new LoadedLibrary(extension, name, Utf8(name));
                loadedLibraries.Add(library);
            }
            if ((flags & RTLD_GLOBAL) != 0) library.Global = true;
            return library.Handle;
        }
    }

    /// <summary>
    /// dlsym: the managed function pointer of the stub the library's extension resolves the symbol to, or 0.
    /// RTLD_DEFAULT and RTLD_NEXT search the libraries opened with RTLD_GLOBAL, in the order they were opened.
    /// </summary>
    [CExport("__llvmnet_dlsym")]
    public static nint DlSym(nint handle, nint symbol)
    {
        string name = symbol == 0 ? "" : Marshal.PtrToStringUTF8(symbol)!;
        lock (loadingGate)
        {
            bool search = handle is RTLD_DEFAULT or RTLD_NEXT;
            List<LoadedLibrary> scope = loadedLibraries.FindAll(loaded => search ? loaded.Global : loaded.Handle == handle);
            if (!search && scope.Count == 0) return InvalidHandle(handle);
            foreach (LoadedLibrary library in scope)
            {
                nint address;
                try { address = library.Extension.Resolve(library.Name, name); }
                catch (Exception error) { return LoadingError($"{library.Extension.Name}: {error.Message}"); }
                if (address == 0) continue;
                resolvedSymbols.TryAdd(address, new ResolvedSymbol(library, name));
                return address;
            }
        }
        return LoadingError("Symbol not found: " + name);
    }

    /// <summary>dlclose: 0 for a handle dlopen returned, which stays valid; 1 for anything else.</summary>
    [CExport("__llvmnet_dlclose")]
    public static int DlClose(nint handle)
    {
        lock (loadingGate)
            if (loadedLibraries.Exists(loaded => loaded.Handle == handle)) return 0;
        InvalidHandle(handle);
        return 1;
    }

    /// <summary>
    /// The length in bytes of the message the calling thread's last failed __llvmnet_dlopen, __llvmnet_dlsym or
    /// __llvmnet_dlclose left (0 when none failed), copied to <paramref name="buffer"/> truncated to size - 1 bytes and
    /// NUL-terminated; nothing is written when <paramref name="size"/> is 0.
    /// </summary>
    [CExport("__llvmnet_dlerror")]
    public static long DlError(nint buffer, long size)
    {
        byte[] message = Encoding.UTF8.GetBytes(loadingError ?? "");
        if (size > 0)
        {
            int length = (int)Math.Min(message.Length, size - 1);
            message.AsSpan(0, length).CopyTo(new Span<byte>((void*)buffer, length));
            ((byte*)buffer)[length] = 0;
        }
        return message.Length;
    }

    /// <summary>
    /// dladdr: describes a stub dlsym returned (the library name as opened, its handle as the base, the symbol and its
    /// address) and returns 1; any other address is in no shared object of a portable image and returns 0.
    /// </summary>
    [CExport("__llvmnet_dladdr")]
    public static int DlAddr(nint address, nint info)
    {
        lock (loadingGate)
        {
            if (!resolvedSymbols.TryGetValue(address, out ResolvedSymbol? symbol)) return 0;
            if (symbol.NativeName == 0) symbol.NativeName = Utf8(symbol.Name);
            *(DlInfo*)info = new DlInfo
            {
                FileName = symbol.Library.Handle,
                Base = symbol.Library.Handle,
                SymbolName = symbol.NativeName,
                SymbolAddress = address
            };
            return 1;
        }
    }

    private static nint LoadingError(string message)
    {
        loadingError = message;
        return 0;
    }

    private static nint InvalidHandle(nint handle) => LoadingError($"Invalid library handle 0x{handle:x}");

    /// <summary>A NUL-terminated UTF-8 copy that lives as long as the process (library and symbol names).</summary>
    private static nint Utf8(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        nint copy = (nint)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>((void*)copy, bytes.Length));
        return copy;
    }
}
