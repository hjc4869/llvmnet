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

    /// <summary>
    /// argv[0] as a native program started the same way sees it. .NET reports the image (program.dll) and the dotnet host,
    /// so a launcher (Toolchain.Launcher in the compiler) passes its own $0 in LLVMNET_ARGV0 and its path, with links
    /// resolved, in LLVMNET_EXE. Both leave the environment here: a native program's environment has no such variables, and
    /// a child process must not take them for its own. On Windows a launcher's name is a host path (a .cmd launcher passes
    /// C:\...\name), which the program sees in its own form (/c/.../name), as it sees every other path.
    /// </summary>
    public static string ProgramName { get; } = TakeVariable("LLVMNET_ARGV0") is string launcher ? HostPaths.ToProgram(launcher)
        : Environment.GetCommandLineArgs().FirstOrDefault() ?? Environment.ProcessPath ?? "program";

    /// <summary>
    /// The host path /proc/self/exe names: the launcher, else the process's executable (the dotnet host, or the NativeAOT
    /// executable). A launcher run by Git for Windows' shell may pass its path in the program's form (/c/...).
    /// </summary>
    public static string? Executable { get; } = TakeVariable("LLVMNET_EXE") is string launcher ? HostPaths.ToHost(launcher) : Environment.ProcessPath;

    private static string? TakeVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null) return null;
        Environment.SetEnvironmentVariable(name, null);
        return value.Length > 0 ? value : null;
    }

    /// <summary>The main thread's stack portable programs get, what RLIMIT_STACK reports where the host cannot tell (8 MB).</summary>
    private const int MainStackSize = 8 << 20;

    /// <summary>
    /// Runs an image's entry point (<paramref name="entry"/>, its <c>int __Entry(string[])</c>). Portable programs are
    /// promised the 8 MB main-thread stack that RLIMIT_STACK reports (docs/dev/host-abi.md), which Linux and macOS give
    /// the main thread; on Windows, where dotnet.exe and NativeAOT executables give it 1.5 MB, the entry point runs on a
    /// thread with 8 MB instead. Large programs need it before main: the precompiled type initializer of an llvmnet-built
    /// clang takes about 2 MB of stack, and compilers recurse deeply on nested code.
    /// </summary>
    public static unsafe int RunEntry(nint entry, string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) return ((delegate*<string[], int>)entry)(arguments);
        int status = 0;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { status = ((delegate*<string[], int>)entry)(arguments); }
            catch (Exception exception) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); }
        }, MainStackSize) { Name = "main" };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return status;
    }

    public static int Initialize(string[] arguments)
    {
        string[] values = [ProgramName, .. arguments];
        Arguments = Strings(values);
        ArgumentCount = values.Length;
        EnvironmentVector = Strings(EnvironmentEntries());
        RestoreRandomSeed();
        RemoveLauncherVariables();
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

    /// <summary>A system-mode program reads the process environment through glibc, which .NET does not change, so the launcher's variables leave it there too.</summary>
    private static void RemoveLauncherVariables()
    {
        if (!AbiContract.SystemCallbacks || !OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var unsetenv = (delegate* unmanaged[Cdecl]<byte*, int>)SystemAbi.Symbol("libc.so.6", "unsetenv");
        fixed (byte* name = "LLVMNET_ARGV0"u8) unsetenv(name);
        fixed (byte* name = "LLVMNET_EXE"u8) unsetenv(name);
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
    /// vector with AT_PAGESZ, AT_RANDOM, equal real/effective ids and the host's AT_HWCAP, which __init_libc and
    /// getauxval read.
    /// </summary>
    public static int InitializeStartup(string[] arguments)
    {
        HostSyscalls.InitializeHost();
        string[] values = [ProgramName, .. arguments];
        string[] environment = EnvironmentEntries();
        int slots = values.Length + 1 + environment.Length + 1 + 2 * 10;
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
        foreach ((nint key, nint value) in new (nint, nint)[] { (6, 4096), (25, random), (11, 0), (12, 0), (13, 0), (14, 0), (23, 0), (16, CpuFeatures.HardwareCapabilities()), (26, CpuFeatures.HardwareCapabilities2()) })
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
