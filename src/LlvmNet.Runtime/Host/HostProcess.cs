using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>
/// Process services: exit, signals, child processes, identity and limits. Signals raised inside the process are
/// delivered synchronously on the raising thread, honoring masks; signals from outside reach installed handlers on a
/// .NET signal thread. posix_spawn, system and popen start host processes through __llvmnet_spawn.
/// </summary>
public static unsafe partial class HostSyscalls
{
    private static readonly object signalGate = new();
    private static readonly SignalAction[] signalActions = new SignalAction[NSIG + 1];
    private static readonly Dictionary<int, PosixSignalRegistration> signalRegistrations = [];
    [ThreadStatic] private static ulong blockedSignals;
    [ThreadStatic] private static ulong pendingSignals;

    private static ulong Bit(int signal) => 1UL << (signal - 1);

    private static long SetSignalAction(int signal, SignalAction* action, SignalAction* old)
    {
        if (signal is <= 0 or > NSIG || action != null && signal is SIGKILL or SIGSTOP) return -EINVAL;
        lock (signalGate)
        {
            if (old != null) *old = signalActions[signal];
            if (action == null) return 0;
            signalActions[signal] = *action;
            UpdateRegistration(signal, action->Handler);
        }
        return 0;
    }

    /// <summary>Connects a handler for a signal the host operating system can send (Ctrl-C, termination) to .NET's signal API.</summary>
    private static void UpdateRegistration(int signal, nint handler)
    {
        PosixSignal? external = signal switch
        {
            SIGINT => PosixSignal.SIGINT,
            SIGTERM => PosixSignal.SIGTERM,
            SIGHUP => PosixSignal.SIGHUP,
            SIGQUIT => PosixSignal.SIGQUIT,
            // Faults belong to the CLR, SIGCHLD to its process support, and real-time signals to its thread suspension.
            SIGILL or SIGTRAP or SIGBUS or SIGFPE or SIGSEGV or SIGCHLD or > 31 => null,
            // On Linux the host's signal numbers are the program's, so any other signal can be passed through raw.
            _ when OperatingSystem.IsLinux() => (PosixSignal)signal,
            _ => null
        };
        if (external is null) return;
        if (signalRegistrations.Remove(signal, out PosixSignalRegistration? previous)) previous.Dispose();
        if (handler == SIG_DFL) return;
        signalRegistrations[signal] = PosixSignalRegistration.Create(external.Value, context =>
        {
            context.Cancel = true;
            // The handler runs on a .NET signal thread, which borrows the main thread's musl thread descriptor.
            if (threadPointer == 0) threadPointer = processThreadPointer;
            Deliver(signal);
        });
    }

    private static long SignalMask(int how, ulong* set, ulong* old)
    {
        if (old != null) *old = blockedSignals;
        if (set == null) return 0;
        ulong mask = *set & ~(Bit(SIGKILL) | Bit(SIGSTOP));
        blockedSignals = how switch
        {
            SIG_BLOCK => blockedSignals | mask,
            SIG_UNBLOCK => blockedSignals & ~mask,
            SIG_SETMASK => mask,
            _ => blockedSignals
        };
        if (how is not (SIG_BLOCK or SIG_UNBLOCK or SIG_SETMASK)) return -EINVAL;
        DeliverPending();
        return 0;
    }

    /// <summary>The calling thread's signal mask, for sigsetjmp and siglongjmp in portable mode (NonLocalJumps).</summary>
    internal static ulong BlockedSignals
    {
        get => blockedSignals;
        set
        {
            blockedSignals = value & ~(Bit(SIGKILL) | Bit(SIGSTOP));
            DeliverPending();
        }
    }

    private static long PendingSignals(ulong* set)
    {
        *set = pendingSignals;
        return 0;
    }

    private static void DeliverPending()
    {
        for (ulong ready; (ready = pendingSignals & ~blockedSignals) != 0;)
        {
            int signal = System.Numerics.BitOperations.TrailingZeroCount(ready) + 1;
            pendingSignals &= ~Bit(signal);
            Deliver(signal);
        }
    }

    /// <summary>kill, tkill and tgkill aimed at this process: a blocked signal stays pending, others are delivered at once.</summary>
    private static long Raise(int signal)
    {
        if (signal == 0) return 0;
        if (signal is < 0 or > NSIG) return -EINVAL;
        if ((blockedSignals & Bit(signal)) != 0 && signal is not (SIGKILL or SIGSTOP))
        {
            pendingSignals |= Bit(signal);
            return 0;
        }
        Deliver(signal);
        return 0;
    }

    private static void Deliver(int signal)
    {
        const ulong NoDefer = 0x40000000;
        SignalAction action;
        lock (signalGate)
        {
            action = signalActions[signal];
            if ((action.Flags & SA_RESETHAND) != 0 && action.Handler > SIG_IGN) signalActions[signal] = default;
        }
        if (action.Handler == SIG_IGN) return;
        if (action.Handler == SIG_DFL)
        {
            if (signal is SIGCHLD or SIGCONT or SIGURG or SIGWINCH) return;      // default action: ignore
            Console.Out.Flush();
            Console.Error.WriteLine(signal == SIGABRT ? "Aborted" : $"Terminated by signal {signal}");
            if (signal == SIGABRT) WriteCallStack();
            Environment.Exit(128 + signal);
        }
        ulong saved = blockedSignals;
        blockedSignals |= action.Mask | ((action.Flags & NoDefer) == 0 ? Bit(signal) : 0);
        try
        {
            if ((action.Flags & SA_SIGINFO) == 0)
                ((delegate* managed<int, void>)action.Handler)(signal);
            else
            {
                // siginfo_t (128 bytes): si_signo, si_errno, si_code, then si_pid and si_uid; an empty ucontext_t.
                byte* information = stackalloc byte[128];
                byte* context = stackalloc byte[1024];
                new Span<byte>(information, 128).Clear();
                new Span<byte>(context, 1024).Clear();
                ((int*)information)[0] = signal;
                ((int*)information)[2] = SI_TKILL;
                ((int*)information)[4] = Environment.ProcessId;
                ((int*)information)[5] = UserId;
                ((delegate* managed<int, nint, nint, void>)action.Handler)(signal, (nint)information, (nint)context);
            }
        }
        finally { blockedSignals = saved; }
        DeliverPending();
    }

    /// <summary>The translated (C, C++ or Fortran) frames of the current thread, innermost first, for abort.</summary>
    private static void WriteCallStack()
    {
        IEnumerable<string> frames = new StackTrace().GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.Name == "Program" && method.Module.Assembly != typeof(HostSyscalls).Assembly)
            .Select(method => method!.Name).Take(64);
        string text = string.Join(Environment.NewLine, frames.Select(name => "    at " + name));
        if (text.Length > 0) Console.Error.WriteLine("C call stack:" + Environment.NewLine + text);
    }

    private static long ExitProcess(int status)
    {
        Console.Out.Flush();
        if (Environment.GetEnvironmentVariable("LLVMNET_CHECK_DESCRIPTORS") == "1") ReportOpenDescriptors();
        Environment.Exit(status & 0xff);
        return 0;
    }

    /// <summary>With LLVMNET_CHECK_DESCRIPTORS=1, descriptors above 2 still open at exit are reported.</summary>
    private static void ReportOpenDescriptors()
    {
        List<string> open;
        lock (gate)
            open = descriptors.Where(entry => entry.Key > 2)
                .Select(entry => $"{entry.Key} ({entry.Value.File.Path ?? entry.Value.File.Kind.ToString().ToLowerInvariant()})").ToList();
        if (open.Count > 0) Console.Error.WriteLine($"llvmnet: {open.Count} descriptors open at exit: {string.Join(", ", open)}");
    }

    private sealed class Child(Process process, Task[] pumps)
    {
        internal readonly Process Process = process;
        internal readonly Task[] Pumps = pumps;
        internal readonly Task Exited = process.WaitForExitAsync();
    }

    private static readonly Dictionary<int, Child> children = [];

    /// <summary>
    /// Starts a host process for musl's posix_spawn (ports/musl/src/process/llvmnet/posix_spawn.c). <paramref name="streams"/>
    /// holds the descriptors the child's standard input, output and error come from after the spawn file actions, or -1 for
    /// closed; descriptors other than the original console streams are connected by copying threads. Returns 0 or an errno.
    /// </summary>
    [CExport("__llvmnet_spawn")]
    public static int Spawn(nint pid, nint path, int search, nint arguments, nint environment, nint streams, nint directory)
    {
        try
        {
            string program = Text(path);
            string? executable = FindExecutable(program, search != 0, environment);
            if (executable is null) return ENOENT;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = directory != 0 ? Path.GetFullPath(Text(directory)) : Directory.GetCurrentDirectory()
            };
            for (nint* argument = (nint*)arguments + 1; arguments != 0 && *argument != 0; argument++)
                start.ArgumentList.Add(Text(*argument));
            if (environment != 0)
            {
                start.Environment.Clear();
                foreach (string variable in Strings(environment))
                {
                    int separator = variable.IndexOf('=');
                    if (separator > 0) start.Environment[variable[..separator]] = variable[(separator + 1)..];
                }
            }
            OpenFile?[] sources = new OpenFile?[3];
            bool[] redirected = new bool[3];
            for (int stream = 0; stream < 3; stream++)
            {
                int descriptor = ((int*)streams)[stream];
                sources[stream] = descriptor < 0 ? null : Lookup(descriptor) ?? throw new HostException(EBADF);
                redirected[stream] = sources[stream] is not { Kind: FileKind.Console } source || source.Console != stream;
            }
            start.RedirectStandardInput = redirected[0];
            start.RedirectStandardOutput = redirected[1];
            start.RedirectStandardError = redirected[2];
            // The copying threads keep their descriptions open, as the child's own descriptors would.
            for (int stream = 0; stream < 3; stream++)
                if (redirected[stream] && sources[stream] is not null) Retain(sources[stream]!);
            Process process;
            try { process = Process.Start(start) ?? throw new HostException(ENOEXEC); }
            catch
            {
                for (int stream = 0; stream < 3; stream++)
                    if (redirected[stream] && sources[stream] is not null) Release(sources[stream]!);
                throw;
            }
            Task[] pumps =
            [
                redirected[0] ? Task.Run(() => FeedInput(sources[0], process.StandardInput.BaseStream)) : Task.CompletedTask,
                redirected[1] ? Task.Run(() => DrainOutput(process.StandardOutput.BaseStream, sources[1])) : Task.CompletedTask,
                redirected[2] ? Task.Run(() => DrainOutput(process.StandardError.BaseStream, sources[2])) : Task.CompletedTask,
            ];
            lock (children) children[process.Id] = new Child(process, pumps);
            *(int*)pid = process.Id;
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or InvalidOperationException)
        {
            return error is Win32Exception { NativeErrorCode: 8 } ? ENOEXEC : Errno(error);
        }
    }

    private static List<string> Strings(nint vector)
    {
        List<string> result = [];
        for (nint* entry = (nint*)vector; *entry != 0; entry++) result.Add(Text(*entry));
        return result;
    }

    /// <summary>execvp's search: a name without a slash is looked up in PATH (from the child's environment), others are paths.</summary>
    private static string? FindExecutable(string program, bool search, nint environment)
    {
        if (program.Contains('/') || !search)
        {
            string full = Path.GetFullPath(program);
            return File.Exists(full) ? full : null;
        }
        string? path = environment == 0 ? Environment.GetEnvironmentVariable("PATH")
            : Strings(environment).FirstOrDefault(variable => variable.StartsWith("PATH=", StringComparison.Ordinal))?[5..];
        foreach (string directory in (path ?? "/usr/local/bin:/bin:/usr/bin").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, program);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void FeedInput(OpenFile? source, Stream input)
    {
        byte[] buffer = new byte[65536];
        try
        {
            while (source is not null)
            {
                int count = (int)(source.Pipe is Pipe pipe ? pipe.Read(buffer, false) : source.Stream?.Read(buffer) ?? 0);
                if (count <= 0) break;
                input.Write(buffer, 0, count);
                input.Flush();
            }
        }
        catch (IOException) { }                                        // the child stopped reading
        finally
        {
            try { input.Dispose(); } catch (IOException) { }
            if (source is not null) Release(source);
        }
    }

    private static void DrainOutput(Stream output, OpenFile? target)
    {
        byte[] buffer = new byte[65536];
        try
        {
            for (int count; (count = output.Read(buffer)) > 0;)
            {
                if (target?.Pipe is Pipe pipe) { if (pipe.Write(buffer.AsSpan(0, count), false) < 0) target = null; }
                else if (target?.Stream is Stream stream)
                    lock (stream)
                    {
                        if ((target.Flags & O_APPEND) != 0 && stream.CanSeek) stream.Seek(0, SeekOrigin.End);
                        stream.Write(buffer, 0, count);
                        stream.Flush();
                    }
            }
        }
        finally
        {
            if (target is not null) Release(target);
        }
    }

    /// <summary>wait4: reaps a child started by __llvmnet_spawn; the status encodes its exit code as WEXITSTATUS.</summary>
    private static long Wait(int pid, int* status, int options)
    {
        Child? child;
        lock (children)
        {
            if (pid > 0) child = children.GetValueOrDefault(pid);
            else child = children.Values.FirstOrDefault(candidate => candidate.Exited.IsCompleted) ?? children.Values.FirstOrDefault();
            if (child is null) return -ECHILD;
        }
        if ((options & WNOHANG) != 0 && !child.Exited.IsCompleted) return 0;
        if (pid <= 0 && !child.Exited.IsCompleted)
        {
            Task[] running;
            lock (children) running = children.Values.Select(candidate => candidate.Exited).ToArray();
            Task.WaitAny(running);
            lock (children) child = children.Values.First(candidate => candidate.Exited.IsCompleted);
        }
        child.Exited.Wait();
        Task.WaitAll(child.Pumps);
        lock (children) children.Remove(child.Process.Id);
        if (status != null) *status = (child.Process.ExitCode & 0xff) << 8;
        int id = child.Process.Id;
        child.Process.Dispose();
        return id;
    }

    private static string UserName => Environment.UserName.Replace(':', '_');
    private static string HomeDirectory => Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The synthetic /etc/passwd: root and the one user every portable program runs as.</summary>
    private static string PasswordFile() => $"root:x:0:0:root:/root:/bin/sh\n{UserName}:x:{UserId}:{GroupId}:{UserName}:{HomeDirectory}:/bin/sh\n";

    private static string GroupFile() => $"root:x:0:\n{UserName}:x:{GroupId}:\n";

    private static long Groups(int count, uint* list)
    {
        if (count == 0) return 1;
        if (count < 0) return -EINVAL;
        list[0] = GroupId;
        return 1;
    }

    private static long Name(byte* buffer)
    {
        string[] fields =
        [
            OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.OSDescription,
            Environment.MachineName, Environment.OSVersion.Version.ToString(), "llvmnet dotnet64-v2",
            RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", var other => other.ToString().ToLowerInvariant() },
            "(none)"
        ];
        // struct utsname: six fields of 65 bytes each.
        new Span<byte>(buffer, 65 * 6).Clear();
        for (int index = 0; index < fields.Length; index++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(fields[index]);
            bytes.AsSpan(0, Math.Min(bytes.Length, 64)).CopyTo(new Span<byte>(buffer + 65 * index, 64));
        }
        return 0;
    }

    /// <summary>Soft and hard limits per resource. RLIMIT_NOFILE bounds descriptor numbers; RLIMIT_AS and RLIMIT_DATA bound mapped memory.</summary>
    private static readonly ulong[,] resourceLimits = CreateLimits();

    private static ulong[,] CreateLimits()
    {
        var limits = new ulong[RLIMIT_COUNT, 2];
        for (int resource = 0; resource < RLIMIT_COUNT; resource++) limits[resource, 0] = limits[resource, 1] = RLIM_INFINITY;
        limits[RLIMIT_STACK, 0] = 8UL << 20;
        limits[RLIMIT_NOFILE, 0] = 1024;
        limits[RLIMIT_NOFILE, 1] = 1UL << 20;
        return limits;
    }

    internal static ulong SoftLimit(int resource)
    {
        lock (resourceLimits) return resourceLimits[resource, 0];
    }

    /// <summary>getrlimit, setrlimit and prlimit64 for this process; raising a hard limit is not permitted, as for an unprivileged user.</summary>
    private static long ResourceLimit(int resource, ulong* limit, ulong* old)
    {
        if (resource is < 0 or >= RLIMIT_COUNT) return -EINVAL;
        lock (resourceLimits)
        {
            if (old != null) (old[0], old[1]) = (resourceLimits[resource, 0], resourceLimits[resource, 1]);
            if (limit == null) return 0;
            if (limit[0] > limit[1]) return -EINVAL;
            if (limit[1] > resourceLimits[resource, 1]) return -EPERM;
            (resourceLimits[resource, 0], resourceLimits[resource, 1]) = (limit[0], limit[1]);
        }
        return 0;
    }

    /// <summary>getrusage: CPU times and peak resident size of this process (also for RUSAGE_THREAD); children report zero.</summary>
    private static long ResourceUsage(int who, long* usage)
    {
        const int Self = 0, Children = -1, Thread = 1;
        if (who is not (Self or Children or Thread)) return -EINVAL;
        new Span<byte>(usage, 144).Clear();
        if (who == Children) return 0;
        using Process process = Process.GetCurrentProcess();
        WriteTimeval(usage, process.UserProcessorTime);
        WriteTimeval(usage + 2, process.PrivilegedProcessorTime);
        usage[4] = process.PeakWorkingSet64 / 1024;                     // ru_maxrss in KiB
        return 0;

        static void WriteTimeval(long* target, TimeSpan time)
        {
            target[0] = Math.DivRem(time.Ticks, TimeSpan.TicksPerSecond, out long remainder);
            target[1] = remainder / 10;
        }
    }

    /// <summary>sysinfo: uptime and memory sizes as .NET reports them, in bytes (mem_unit 1).</summary>
    private static long SystemInformation(byte* information)
    {
        new Span<byte>(information, 112).Clear();
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        *(long*)information = Environment.TickCount64 / 1000;
        *(ulong*)(information + 32) = (ulong)total;
        using (Process process = Process.GetCurrentProcess())
            *(ulong*)(information + 40) = (ulong)Math.Max(0, total - process.WorkingSet64);
        *(ushort*)(information + 80) = 1;
        *(uint*)(information + 104) = 1;
        return 0;
    }

    /// <summary>times: CPU time in clock ticks of 1/100 s; the result is the uptime in ticks.</summary>
    private static long ProcessTimes(long* times)
    {
        if (times != null)
        {
            using Process process = Process.GetCurrentProcess();
            times[0] = process.UserProcessorTime.Ticks / 100_000;
            times[1] = process.PrivilegedProcessorTime.Ticks / 100_000;
            times[2] = times[3] = 0;
        }
        return Environment.TickCount64 / 10;
    }
}
