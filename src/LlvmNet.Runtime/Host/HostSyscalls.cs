using System.ComponentModel;
using System.Runtime.InteropServices;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>
/// The host layer under musl in the portable sysroot (plan items LC-04 to LC-12, contract in docs/dev/host-abi.md).
/// musl's arch/llvmnet/syscall_arch.h sends every system call to <see cref="Syscall"/>. Numbers, arguments,
/// structure layouts and results follow Linux x86-64, including the rule that a failing call returns -errno, so
/// musl runs unmodified. The implementation uses .NET APIs only and behaves the same on every .NET host.
/// The parts live in Host/: descriptors and pipes, the file system, memory mappings, processes and signals,
/// clocks, and threads.
/// </summary>
public static unsafe partial class HostSyscalls
{
    [CExport("__llvmnet_syscall")]
    public static long Syscall(long number, long a, long b, long c, long d, long e, long f)
    {
        try
        {
            return (Linux.Syscall)number switch
            {
                Linux.Syscall.Read => Read((int)a, (nint)b, c),
                Linux.Syscall.Write => Write((int)a, (nint)b, c),
                Linux.Syscall.Open => Open(AT_FDCWD, (nint)a, (int)b, (int)c),
                Linux.Syscall.Close => Close((int)a),
                Linux.Syscall.Stat => StatPath(AT_FDCWD, (nint)a, (nint)b, 0),
                Linux.Syscall.Fstat => StatPath((int)a, 0, (nint)b, AT_EMPTY_PATH),
                Linux.Syscall.Lstat => StatPath(AT_FDCWD, (nint)a, (nint)b, AT_SYMLINK_NOFOLLOW),
                Linux.Syscall.Poll => Poll((PollDescriptor*)a, (int)b, (int)c),
                Linux.Syscall.Lseek => Seek((int)a, b, (int)c),
                Linux.Syscall.Mmap => Map((nint)a, b, (int)c, (int)d, (int)e, f),
                Linux.Syscall.Mprotect or Linux.Syscall.Mlock or Linux.Syscall.Munlock or Linux.Syscall.Mlockall or
                    Linux.Syscall.Munlockall => 0,                     // no memory protection or paging control
                Linux.Syscall.Munmap => Unmap((nint)a, b),
                Linux.Syscall.Brk => 0,                                // report no heap, so mallocng uses mmap
                Linux.Syscall.RtSigaction => SetSignalAction((int)a, (SignalAction*)b, (SignalAction*)c),
                Linux.Syscall.RtSigprocmask => SignalMask((int)a, (ulong*)b, (ulong*)c),
                Linux.Syscall.Ioctl => Control((int)a, b, (nint)c),
                Linux.Syscall.Pread64 => ReadAt((int)a, (nint)b, c, d),
                Linux.Syscall.Pwrite64 => WriteAt((int)a, (nint)b, c, d),
                Linux.Syscall.Readv => Vector((int)a, (nint)b, (int)c, read: true, offset: -1),
                Linux.Syscall.Writev => Vector((int)a, (nint)b, (int)c, read: false, offset: -1),
                Linux.Syscall.Preadv or Linux.Syscall.Preadv2 => Vector((int)a, (nint)b, (int)c, read: true, offset: d),
                Linux.Syscall.Pwritev or Linux.Syscall.Pwritev2 => Vector((int)a, (nint)b, (int)c, read: false, offset: d),
                Linux.Syscall.Access => Access(AT_FDCWD, (nint)a, (int)b),
                Linux.Syscall.Pipe => CreatePipe((int*)a, 0),
                Linux.Syscall.Pipe2 => CreatePipe((int*)a, (int)b),
                Linux.Syscall.Select => Select((int)a, (ulong*)b, (ulong*)c, (ulong*)d, e == 0 ? -1 : MillisecondsFromTimeval((long*)e)),
                Linux.Syscall.Pselect6 => Select((int)a, (ulong*)b, (ulong*)c, (ulong*)d, e == 0 ? -1 : MillisecondsFromTimespec((Timespec*)e)),
                Linux.Syscall.Ppoll => Poll((PollDescriptor*)a, (int)b, c == 0 ? -1 : MillisecondsFromTimespec((Timespec*)c)),
                Linux.Syscall.SchedYield => Yield(),
                Linux.Syscall.Mremap => Remap((nint)a, b, c, (int)d),
                Linux.Syscall.Msync => Synchronize((nint)a, b),
                Linux.Syscall.Madvise => Advise((nint)a, b, (int)c),
                Linux.Syscall.Dup => Duplicate((int)a, -1, 0),
                Linux.Syscall.Dup2 => (int)a == (int)b ? (Lookup((int)a) is null ? -EBADF : b) : Duplicate((int)a, (int)b, 0),
                Linux.Syscall.Dup3 => (int)a == (int)b ? -EINVAL : Duplicate((int)a, (int)b, (int)c),
                Linux.Syscall.Nanosleep => Sleep((Timespec*)a),
                Linux.Syscall.Getpid => Environment.ProcessId,
                Linux.Syscall.Fork or Linux.Syscall.Vfork or Linux.Syscall.Execve => -ENOSYS,   // posix_spawn goes to __llvmnet_spawn
                Linux.Syscall.Exit => ExitThread((int)a),
                Linux.Syscall.Wait4 => Wait((int)a, (int*)b, (int)c),
                Linux.Syscall.Kill => (int)a == Environment.ProcessId || (int)a == 0 || (int)a == -1 ? Raise((int)b) : -ESRCH,
                Linux.Syscall.Tkill => SendSignal((int)a, (int)b),
                Linux.Syscall.Tgkill => SendSignal((int)b, (int)c),
                Linux.Syscall.Uname => Name((byte*)a),
                Linux.Syscall.Fcntl => FileControl((int)a, (int)b, c),
                Linux.Syscall.Flock => Lookup((int)a) is null ? -EBADF : 0,   // advisory locks always succeed in one process
                Linux.Syscall.Fsync or Linux.Syscall.Fdatasync => Flush((int)a),
                Linux.Syscall.Sync or Linux.Syscall.Syncfs => 0,
                Linux.Syscall.Truncate => TruncatePath((nint)a, b),
                Linux.Syscall.Ftruncate => Truncate((int)a, b),
                Linux.Syscall.Fallocate => Allocate((int)a, (int)b, c, d),
                Linux.Syscall.Fadvise64 => Lookup((int)a) is null ? -EBADF : 0,
                Linux.Syscall.Getcwd => CurrentDirectory((byte*)a, b),
                Linux.Syscall.Chdir => ChangeDirectory((nint)a),
                Linux.Syscall.Fchdir => ChangeDirectory((int)a),
                Linux.Syscall.Rename => Rename(AT_FDCWD, (nint)a, AT_FDCWD, (nint)b, 0),
                Linux.Syscall.Renameat => Rename((int)a, (nint)b, (int)c, (nint)d, 0),
                Linux.Syscall.Renameat2 => Rename((int)a, (nint)b, (int)c, (nint)d, (int)e),
                Linux.Syscall.Mkdir => MakeDirectory(AT_FDCWD, (nint)a, (int)b),
                Linux.Syscall.Mkdirat => MakeDirectory((int)a, (nint)b, (int)c),
                Linux.Syscall.Rmdir => Unlink(AT_FDCWD, (nint)a, AT_REMOVEDIR),
                Linux.Syscall.Unlink => Unlink(AT_FDCWD, (nint)a, 0),
                Linux.Syscall.Unlinkat => Unlink((int)a, (nint)b, (int)c),
                Linux.Syscall.Link or Linux.Syscall.Linkat => -EPERM,  // .NET cannot create hard links
                Linux.Syscall.Symlink => SymbolicLink((nint)a, AT_FDCWD, (nint)b),
                Linux.Syscall.Symlinkat => SymbolicLink((nint)a, (int)b, (nint)c),
                Linux.Syscall.Readlink => ReadLink(AT_FDCWD, (nint)a, (byte*)b, c),
                Linux.Syscall.Readlinkat => ReadLink((int)a, (nint)b, (byte*)c, d),
                Linux.Syscall.Chmod => ChangeMode(AT_FDCWD, (nint)a, (int)b),
                Linux.Syscall.Fchmod => ChangeMode((int)a, 0, (int)b),
                Linux.Syscall.Fchmodat => ChangeMode((int)a, (nint)b, (int)c),
                Linux.Syscall.Chown or Linux.Syscall.Lchown => ChangeOwner(AT_FDCWD, (nint)a, (int)b, (int)c),
                Linux.Syscall.Fchown => ChangeOwner((int)a, 0, (int)b, (int)c),
                Linux.Syscall.Fchownat => ChangeOwner((int)a, (nint)b, (int)c, (int)d),
                Linux.Syscall.Mknod or Linux.Syscall.Mknodat => -EPERM,  // no device files or named pipes
                Linux.Syscall.Umask => SetFileCreationMask((int)a),
                Linux.Syscall.Gettimeofday => TimeOfDay((long*)a),
                Linux.Syscall.Getrusage => ResourceUsage((int)a, (long*)b),
                Linux.Syscall.Sysinfo => SystemInformation((byte*)a),
                Linux.Syscall.Times => ProcessTimes((long*)a),
                Linux.Syscall.Getuid or Linux.Syscall.Geteuid => UserId,
                Linux.Syscall.Getgid or Linux.Syscall.Getegid => GroupId,
                Linux.Syscall.Setuid or Linux.Syscall.Setgid => (int)a is UserId or GroupId ? 0 : -EPERM,
                Linux.Syscall.Getgroups => Groups((int)a, (uint*)b),
                Linux.Syscall.Getppid => 1,
                Linux.Syscall.Getpgrp or Linux.Syscall.Getpgid or Linux.Syscall.Getsid => Environment.ProcessId,
                Linux.Syscall.Setpgid or Linux.Syscall.Setsid => -EPERM,
                Linux.Syscall.Getpriority => 20,                       // nice value 0, offset by 20 as the kernel reports it
                Linux.Syscall.Setpriority => 0,
                Linux.Syscall.RtSigpending => PendingSignals((ulong*)a),
                Linux.Syscall.RtSigtimedwait => -EAGAIN,
                Linux.Syscall.RtSigsuspend => -EINTR,
                Linux.Syscall.Sigaltstack => 0,
                Linux.Syscall.Statfs => FileSystemStatusOf(AT_FDCWD, (nint)a, (FileSystemStatus*)b),
                Linux.Syscall.Fstatfs => FileSystemStatusOf((int)a, 0, (FileSystemStatus*)b),
                Linux.Syscall.Prctl => 0,
                Linux.Syscall.ArchPrctl => b == ARCH_SET_FS ? SetThreadPointer((nint)b) : -EINVAL,
                Linux.Syscall.Setrlimit => ResourceLimit((int)a, (ulong*)b, null),
                Linux.Syscall.Prlimit64 => (int)a is 0 || (int)a == Environment.ProcessId ? ResourceLimit((int)b, (ulong*)c, (ulong*)d) : -ESRCH,
                Linux.Syscall.SetRobustList => SetRobustList((nint)a, b),
                Linux.Syscall.GetRobustList => GetRobustList((int)a, (nint*)b, (long*)c),
                Linux.Syscall.Gettid => CurrentThreadId,
                Linux.Syscall.Time => Time((long*)a),
                Linux.Syscall.Futex => Futex((nint)a, (int)b, (int)c, (Timespec*)d),
                Linux.Syscall.SchedGetaffinity => Affinity((byte*)c, b),
                Linux.Syscall.SetThreadArea => SetThreadPointer((nint)a),
                Linux.Syscall.Getdents64 => DirectoryEntries((int)a, (byte*)b, (int)c),
                Linux.Syscall.SetTidAddress => CurrentThreadId,
                Linux.Syscall.ClockGettime => ClockTime((int)a, (Timespec*)b),
                Linux.Syscall.ClockGetres => ClockResolution((int)a, (Timespec*)b),
                Linux.Syscall.ClockNanosleep => ClockSleep((int)a, (int)b, (Timespec*)c),
                Linux.Syscall.ExitGroup => ExitProcess((int)a),
                Linux.Syscall.Utimes => SetTimes(AT_FDCWD, (nint)a, (long*)b, microseconds: true, 0),
                Linux.Syscall.Utimensat => SetTimes((int)a, (nint)b, (long*)c, microseconds: false, (int)d),
                Linux.Syscall.Openat => Open((int)a, (nint)b, (int)c, (int)d),
                Linux.Syscall.Newfstatat => StatPath((int)a, (nint)b, (nint)c, (int)d),
                Linux.Syscall.Faccessat or Linux.Syscall.Faccessat2 => Access((int)a, (nint)b, (int)c),
                Linux.Syscall.Getrandom => Random((byte*)a, b),
                Linux.Syscall.Membarrier => 0,
                _ => -ENOSYS
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            return -Errno(error);
        }
        catch (ThreadInterruptedException)
        {
            // A blocking call of a thread that pthread_cancel (SIGCANCEL) or pthread_kill woke up.
            return -EINTR;
        }
    }

    /// <summary>The single mapping from .NET exceptions to Linux errno values.</summary>
    internal static int Errno(Exception error) => error switch
    {
        HostException host => host.Errno,
        FileNotFoundException or DirectoryNotFoundException => ENOENT,
        UnauthorizedAccessException => EACCES,
        PathTooLongException => ENAMETOOLONG,
        ArgumentException => EINVAL,
        NotSupportedException => EINVAL,
        Win32Exception { NativeErrorCode: 2 } => ENOENT,
        Win32Exception { NativeErrorCode: 13 } => EACCES,
        // On Linux the runtime reports the kernel's errno for other I/O failures; other hosts number errors differently.
        IOException io when OperatingSystem.IsLinux() && (io.HResult & 0xffff) is > 0 and < 4096 => io.HResult & 0xffff,
        IOException io when io.HResult == unchecked((int)0x80070070) => ENOSPC,
        _ => EIO
    };

    private static string Text(nint pointer) => pointer == 0 ? throw new ArgumentException("null path") : Marshal.PtrToStringUTF8(pointer)!;

    /// <summary>The absolute host path for a path argument relative to a directory descriptor, as the *at calls take it.</summary>
    private static string Resolve(int directory, nint pathPointer)
    {
        string path = Text(pathPointer);
        if (path.Length == 0) throw new FileNotFoundException(path);
        if (path.Length >= 4096) throw new PathTooLongException();
        if (Path.IsPathRooted(path)) return path;
        if (directory == AT_FDCWD) return Path.GetFullPath(path);
        OpenFile parent = Lookup(directory) ?? throw new HostException(EBADF);
        if (parent.Kind != FileKind.Directory) throw new HostException(ENOTDIR);
        return Path.GetFullPath(Path.Combine(parent.Path!, path));
    }

    /// <summary>The errno for a path that does not exist: ENOTDIR when a parent component is not a directory.</summary>
    private static long Missing(string path)
    {
        for (string? parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (File.Exists(parent)) return -ENOTDIR;
            if (Directory.Exists(parent)) break;
        }
        return -ENOENT;
    }

    /// <summary>A failure with a known Linux errno, raised where a helper cannot return -errno itself.</summary>
    private sealed class HostException(int errno) : IOException($"errno {errno}")
    {
        internal int Errno { get; } = errno;
    }
}
