using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

namespace LlvmNet.Runtime;

public static unsafe class ProcessRuntime
{
    private static readonly List<nint> argumentAllocations = [];
    private static readonly Stack<nint> exitCallbacks = [];
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
        Cxx.FinalizeModule(0);
        while (exitCallbacks.TryPop(out nint callback))
        {
            if (AbiContract.SystemCallbacks) ((delegate* unmanaged[Cdecl]<void>)callback)();
            else ((delegate* managed<void>)callback)();
        }
        foreach (nint pointer in argumentAllocations)
            NativeMemory.Free((void*)pointer);
        argumentAllocations.Clear();
        Arguments = 0;
        EnvironmentVector = 0;
        ArgumentCount = 0;
        Stdio.Flush(0);
        Console.Out.Flush();
        Console.Error.Flush();
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
        exitCallbacks.Push(callback);
        return 0;
    }

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