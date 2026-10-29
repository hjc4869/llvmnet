using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

namespace LlvmNet.Runtime;

public static unsafe class ProcessRuntime
{
    private static readonly List<nint> argumentAllocations = [];
    private static int cleanedUp;
    private static readonly Dictionary<string, nint> environmentStrings = new(StringComparer.Ordinal);
    [ThreadStatic] private static nint errno;
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
    /// For musl (LC-01): one Linux-style start vector, argv entries, NULL, environment entries, NULL, then an
    /// auxiliary vector with AT_PAGESZ, AT_RANDOM and equal real/effective ids, which musl's __init_libc reads.
    /// </summary>
    public static int InitializeStartup(string[] arguments)
    {
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
        RandomNumberGenerator.Fill(new Span<byte>((void*)random, 16));
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
        Stdio.Flush(0);
        Console.Out.Flush();
        Console.Error.Flush();
        if (AbiContract.SystemCallbacks) SystemAbi.Flush();
    }

    [CExport("__errno_location"), CExport("__llvmnet_errno_location")]
    public static nint ErrnoLocation()
    {
        if (errno == 0)
            errno = (nint)NativeMemory.AllocZeroed(4);
        return errno;
    }

    public static int Error(int value)
    {
        *(int*)ErrnoLocation() = value;
        return -1;
    }

    [CExport("getentropy")]
    public static int Getentropy(nint destination, long size)
    {
        if (size < 0 || size > 256)
            return Error(5);
        RandomNumberGenerator.Fill(new Span<byte>((void*)destination, (int)size));
        return 0;
    }

    [CExport("sysconf")]
    public static long Sysconf(int name) => name switch
    {
        2 => 100,
        30 => Environment.SystemPageSize,
        83 or 84 => Environment.ProcessorCount,
        _ => Error(22)
    };

    [CExport("getenv")]
    public static nint Getenv(nint name)
    {
        string key = CString.Text(name);
        string? value = Environment.GetEnvironmentVariable(key);
        if (value is null)
            return 0;
        lock (environmentStrings)
        {
            if (!environmentStrings.TryGetValue(key, out nint pointer))
            {
                pointer = Marshal.StringToCoTaskMemUTF8(value);
                environmentStrings[key] = pointer;
            }
            return pointer;
        }
    }

    [CExport("setenv")]
    public static int Setenv(nint name, nint value, int overwrite)
    {
        string key = CString.Text(name);
        if (key.Length == 0 || key.Contains('='))
            return Error(22);
        lock (environmentStrings)
        {
            if (overwrite == 0 && Environment.GetEnvironmentVariable(key) is not null)
                return 0;
            Environment.SetEnvironmentVariable(key, CString.Text(value));
            if (environmentStrings.Remove(key, out nint old))
                Marshal.FreeCoTaskMem(old);
        }
        return 0;
    }

    [CExport("unsetenv")]
    public static int Unsetenv(nint name)
    {
        string key = CString.Text(name);
        if (key.Length == 0 || key.Contains('='))
            return Error(22);
        lock (environmentStrings)
        {
            Environment.SetEnvironmentVariable(key, null);
            if (environmentStrings.Remove(key, out nint old))
                Marshal.FreeCoTaskMem(old);
        }
        return 0;
    }

    [CExport("atexit")]
    public static int AtExit(nint callback)
    {
        ExitHandlers.Register(callback, 0, 0, false);
        return 0;
    }

    /// <summary>Registers an llvm.global_dtors entry; emitted images call this in ascending priority order.</summary>
    public static void AtFini(nint callback) => ExitHandlers.RegisterFinalizer(callback);

    [CExport("exit")]
    public static void Exit(int result)
    {
        Cleanup();
        Environment.Exit(result);
    }

    [CExport("_Exit"), CExport("_exit")]
    public static void ImmediateExit(int result) => Environment.Exit(result);

    [CExport("abort")]
    public static void Abort() => throw new InvalidOperationException("C program called abort().");

    [CExport("__assert_fail"), CExport("__llvmnet_assert_fail")]
    public static void AssertFail(nint assertion, nint file, int line, nint function) =>
        throw new InvalidOperationException($"{CString.Text(file)}:{line}: {CString.Text(function)}: assertion {CString.Text(assertion)} failed");
}
