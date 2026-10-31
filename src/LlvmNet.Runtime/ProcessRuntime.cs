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
        EnvironmentVector = Strings(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(item => $"{item.Key}={item.Value}").ToArray());
        return values.Length;
    }

    /// <summary>
    /// Builds musl's Linux-style start vector: argv entries, NULL, environment entries, NULL, then an auxiliary
    /// vector with AT_PAGESZ, AT_RANDOM and equal real/effective ids, which __init_libc reads.
    /// </summary>
    public static int InitializeStartup(string[] arguments)
    {
        HostSyscalls.InitializeHost();
        string[] values = [Environment.GetCommandLineArgs().FirstOrDefault() ?? Environment.ProcessPath ?? "program", .. arguments];
        string[] environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(item => $"{item.Key}={item.Value}").ToArray();
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

    /// <summary>libstdc++'s assertion failure (system mode C++ through the bridge).</summary>
    public static void AssertFail(nint assertion, nint file, int line, nint function) =>
        throw new InvalidOperationException($"{CString.Text(file)}:{line}: {CString.Text(function)}: assertion {CString.Text(assertion)} failed");
}
