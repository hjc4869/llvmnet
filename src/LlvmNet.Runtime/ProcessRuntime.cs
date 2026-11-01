using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Runtime;

/// <summary>
/// Process start and end for emitted images: argv and environment vectors (musl's start vector in portable mode),
/// and in system mode the managed atexit/exit path that runs C and C++ handlers before the process ends.
/// </summary>
public static unsafe class ProcessRuntime
{
    private static readonly List<nint> argumentAllocations = [];
    private static int cleanedUp;
    public static nint Arguments { get; private set; }
    public static nint EnvironmentVector { get; private set; }
    public static int ArgumentCount { get; private set; }

    public static int Initialize(string[] arguments)
    {
        string[] values = [Environment.GetCommandLineArgs().FirstOrDefault() ?? Environment.ProcessPath ?? "program", .. arguments];
        Arguments = Strings(values);
        ArgumentCount = values.Length;
        EnvironmentVector = Strings(EnvironmentEntries());
        RestoreRandomSeed();
        return values.Length;
    }

    /// <summary>
    /// A system-mode program shares the C library with the .NET runtime, and CoreCLR seeds glibc's generator with
    /// srandom(time) while it starts. C requires rand() before any srand() to give the sequence that srand(1) gives, and
    /// a native program gets it; so seed with 1 again before main (glibc's srand and srandom share one state).
    /// </summary>
    private static void RestoreRandomSeed()
    {
        if (!AbiContract.SystemCallbacks || !OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        ((delegate* unmanaged[Cdecl]<uint, void>)SystemAbi.Symbol("libc.so.6", "srandom"))(1);
    }

    /// <summary>
    /// The process environment as "name=value" strings. A loop rather than LINQ: this runs at every start, and each
    /// generic LINQ method it would instantiate is another JIT compilation before main (PF-10).
    /// </summary>
    private static string[] EnvironmentEntries()
    {
        System.Collections.IDictionary variables = Environment.GetEnvironmentVariables();
        var entries = new string[variables.Count];
        int index = 0;
        foreach (System.Collections.DictionaryEntry item in variables) entries[index++] = $"{item.Key}={item.Value}";
        return entries;
    }

    /// <summary>
    /// Builds musl's Linux-style start vector: argv entries, NULL, environment entries, NULL, then an auxiliary
    /// vector with AT_PAGESZ, AT_RANDOM and equal real/effective ids, which __init_libc reads.
    /// </summary>
    public static int InitializeStartup(string[] arguments)
    {
        HostSyscalls.InitializeHost();
        string[] values = [Environment.GetCommandLineArgs().FirstOrDefault() ?? Environment.ProcessPath ?? "program", .. arguments];
        string[] environment = EnvironmentEntries();
        int slots = values.Length + 1 + environment.Length + 1 + 2 * 8;
        nint vector = (nint)NativeMemory.AllocZeroed((nuint)slots, (nuint)IntPtr.Size);
        argumentAllocations.Add(vector);
        nint* slot = (nint*)vector;
        foreach (string value in values) *slot++ = String(value);
        slot++;
        EnvironmentVector = (nint)slot;
        foreach (string value in environment) *slot++ = String(value);
        slot++;
        nint random = (nint)NativeMemory.Alloc(16);
        argumentAllocations.Add(random);
        HostRandom.Fill(new Span<byte>((void*)random, 16));
        foreach ((nint key, nint value) in new (nint, nint)[] { (6, 4096), (25, random), (11, 0), (12, 0), (13, 0), (14, 0), (23, 0) })
        {
            *slot++ = key;
            *slot++ = value;
        }
        Arguments = vector;
        ArgumentCount = values.Length;
        return values.Length;

        static nint String(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            nint text = (nint)NativeMemory.Alloc((nuint)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>((void*)text, bytes.Length));
            ((byte*)text)[bytes.Length] = 0;
            argumentAllocations.Add(text);
            return text;
        }
    }

    private static nint Strings(string[] values)
    {
        nint array = (nint)NativeMemory.AllocZeroed((nuint)(values.Length + 1), (nuint)IntPtr.Size);
        argumentAllocations.Add(array);
        for (int index = 0; index < values.Length; index++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(values[index]);
            nint text = (nint)NativeMemory.Alloc((nuint)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>((void*)text, bytes.Length));
            ((byte*)text)[bytes.Length] = 0;
            ((nint*)array)[index] = text;
            argumentAllocations.Add(text);
        }
        return array;
    }

    public static void Cleanup()
    {
        if (Interlocked.Exchange(ref cleanedUp, 1) != 0) return;
        ThreadStorage.RunDestructors();
        ExitHandlers.Run(0);
        ExitHandlers.RunFinalizers();
        foreach (nint pointer in argumentAllocations)
            NativeMemory.Free((void*)pointer);
        ThreadStorage.Release();
        argumentAllocations.Clear();
        Arguments = 0;
        EnvironmentVector = 0;
        ArgumentCount = 0;
        Console.Out.Flush();
        Console.Error.Flush();
        if (AbiContract.SystemCallbacks) SystemAbi.Flush();
    }

    /// <summary>atexit in system mode (a HostInterop adapter), so handlers run before managed cleanup.</summary>
    public static int AtExit(nint callback)
    {
        ExitHandlers.Register(callback, 0, 0, false);
        return 0;
    }

    /// <summary>Registers an llvm.global_dtors entry; emitted images call this in ascending priority order.</summary>
    public static void AtFini(nint callback) => ExitHandlers.RegisterFinalizer(callback);

    /// <summary>exit in system mode (a HostInterop adapter): managed handlers first, then the process ends.</summary>
    public static void Exit(int result)
    {
        Cleanup();
        Environment.Exit(result);
    }

    private static int sharedCode = -1;

    /// <summary>
    /// fork and vfork in system mode (HostInterop adapters). A forked child goes on running translated code, so it
    /// needs a private copy of the code the runtime generated. Under the JIT, CoreCLR's W^X maps that code from a shared
    /// memory object, so a child and its parent would write their compiled methods and stubs into the same pages: the
    /// call fails with ENOSYS while such a mapping exists, as fork does in portable mode. NativeAOT, which generates no
    /// code, and the JIT with DOTNET_EnableWriteXorExecute=0 call the host's fork. vfork gets fork's semantics, which
    /// POSIX permits: its child would overwrite the frames that the suspended parent returns through.
    /// </summary>
    public static int Fork() => SharedCode() ? Unavailable() : ((delegate* unmanaged[Cdecl]<int>)SystemAbi.Symbol("libc.so.6", "fork"))();

    /// <summary>_Fork, which runs no atfork handlers; see <see cref="Fork"/>.</summary>
    public static int ForkWithoutHandlers() => SharedCode() ? Unavailable() : ((delegate* unmanaged[Cdecl]<int>)SystemAbi.Symbol("libc.so.6", "_Fork"))();

    /// <summary>forkpty, which forks inside the C library; see <see cref="Fork"/>.</summary>
    public static int ForkPty(nint master, nint name, nint terminal, nint size) => SharedCode() ? Unavailable() :
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)SystemAbi.Symbol("libc.so.6", "forkpty"))(master, name, terminal, size);

    private static int Unavailable()
    {
        Marshal.SetLastSystemError(Linux.ENOSYS);
        return -1;
    }

    /// <summary>
    /// Whether the process has a shared executable mapping, which is how CoreCLR's W^X maps generated code. The answer
    /// does not change while the process runs: W^X is fixed at startup, and the JIT has compiled code by the first call.
    /// </summary>
    private static bool SharedCode()
    {
        if (sharedCode < 0)
        {
            int shared = 0;
            foreach (string line in File.ReadLines("/proc/self/maps"))
            {
                int start = line.IndexOf(' ') + 1;
                if (start > 0 && line.Length >= start + 4 && line[start + 2] == 'x' && line[start + 3] == 's') { shared = 1; break; }
            }
            sharedCode = shared;
        }
        return sharedCode == 1;
    }

    /// <summary>libstdc++'s assertion failure (system mode C++ through the bridge).</summary>
    public static void AssertFail(nint assertion, nint file, int line, nint function) =>
        throw new InvalidOperationException($"{CString.Text(file)}:{line}: {CString.Text(function)}: assertion {CString.Text(assertion)} failed");
}
