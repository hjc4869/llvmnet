using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Runtime;

public static unsafe class Signals
{
    private sealed record Registration(nint Handler, bool Reset, PosixSignalRegistration? Native);
    private static readonly Dictionary<int, Registration> registrations = [];
    private static readonly object gate = new();

    [CExport("signal")]
    public static nint Signal(int signal, nint handler) => Register(signal, handler, false);
    [CExport("__sysv_signal")]
    public static nint SysvSignal(int signal, nint handler) => Register(signal, handler, true);
    private static nint Register(int signal, nint handler, bool reset)
    {
        if (signal is <= 0 or >= 65 or 9 or 19)
        {
            ProcessRuntime.Error(22);
            return -1;
        }
        lock (gate)
        {
            nint previous = registrations.TryGetValue(signal, out Registration? old) ? old.Handler : 0;
            try
            {
                PosixSignal? nativeSignal = AbiContract.Portable ? signal switch
                {
                    1 => PosixSignal.SIGHUP,
                    2 => PosixSignal.SIGINT,
                    3 => PosixSignal.SIGQUIT,
                    15 => PosixSignal.SIGTERM,
                    _ => null
                } : (PosixSignal)signal;
                PosixSignalRegistration? registration = handler == 0 || nativeSignal is null ? null : PosixSignalRegistration.Create(nativeSignal.Value, context =>
                {
                    context.Cancel = true;
                    Dispatch(signal);
                });
                old?.Native?.Dispose();
                registrations[signal] = new Registration(handler, reset, registration);
                return previous;
            }
            catch (Exception error) when (error is ArgumentException or PlatformNotSupportedException)
            {
                ProcessRuntime.Error(22);
                return -1;
            }
        }
    }
    private static bool Dispatch(int signal)
    {
        nint handler;
        lock (gate)
        {
            if (!registrations.TryGetValue(signal, out Registration? registration) || registration.Handler == 0)
                return false;
            handler = registration.Handler;
            if (registration.Reset && handler != 1)
            {
                registration.Native?.Dispose();
                registrations.Remove(signal);
            }
        }
        if (handler != 1)
            ((delegate* managed<int, void>)handler)(signal);
        return true;
    }
    [CExport("raise")]
    public static int Raise(int signal)
    {
        if (signal is <= 0 or >= 65) return ProcessRuntime.Error(22);
        if (Dispatch(signal)) return 0;
        throw new InvalidOperationException($"C program raised unhandled signal {signal}.");
    }

    [CExport("backtrace")]
    public static int Backtrace(nint buffer, int maximum)
    {
        StackFrame[] frames = new StackTrace(1).GetFrames();
        int count = Math.Min(maximum, frames.Length);
        for (int index = 0; index < count; index++)
        {
            try { ((nint*)buffer)[index] = frames[index].GetMethod()?.MethodHandle.GetFunctionPointer() ?? 0; }
            catch (InvalidOperationException) { ((nint*)buffer)[index] = 0; }
        }
        return count;
    }
    [CExport("backtrace_symbols")]
    public static nint BacktraceSymbols(nint buffer, int count)
    {
        byte[][] names = Enumerable.Range(0, count).Select(index => Encoding.UTF8.GetBytes($"managed [0x{((nint*)buffer)[index]:x}]\0")).ToArray();
        long size = checked(count * 8L + names.Sum(name => name.Length));
        nint memory = CString.Malloc(size);
        if (memory == 0) return 0;
        nint cursor = memory + count * 8;
        for (int index = 0; index < count; index++)
        {
            ((nint*)memory)[index] = cursor;
            names[index].CopyTo(new Span<byte>((void*)cursor, names[index].Length));
            cursor += names[index].Length;
        }
        return memory;
    }
}