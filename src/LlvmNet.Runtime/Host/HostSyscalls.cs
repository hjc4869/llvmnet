using System.ComponentModel;
using System.Runtime.InteropServices;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>
/// The host layer under musl in the portable sysroot (contract in docs/dev/host-abi.md). musl's
/// arch/llvmnet/syscall_arch.h sends every system call to <see cref="Syscall"/>, whose numbers, arguments, layouts
/// and -errno results follow Linux x86-64, so musl runs unmodified. It uses .NET APIs only and behaves the same on
/// every .NET host.
/// </summary>
public static unsafe partial class HostSyscalls
{
    /// <summary>One service the host cannot provide: the calls, the errno they return and the reason, for docs/user/limitations.md.</summary>
    internal sealed record UnavailableService(string Area, string Calls, string Errno, string Reason, params Linux.Syscall[] Numbers);

    /// <summary>
    /// The services .NET cannot provide, refused by name with the errno a program sees. Everything else that is not
    /// implemented returns -ENOSYS without a name. scripts/limitations.py generates the user-facing table from this
    /// list through "llvmnet --print-host-limitations" (TC-17); docs/dev/host-abi.md states the contract.
    /// </summary>
    internal static readonly UnavailableService[] Unavailable =
    [
        new("Processes", "fork, vfork, clone, clone3", "ENOSYS", ".NET cannot duplicate a process or create a raw thread; posix_spawn, system and popen start child processes, and pthread_create goes through the host's thread entry point", Linux.Syscall.Fork, Linux.Syscall.Vfork, Linux.Syscall.Clone, Linux.Syscall.Clone3),
        new("Processes", "execve, execveat", "ENOSYS", "a .NET process cannot replace its image; posix_spawn starts the program as a child", Linux.Syscall.Execve, Linux.Syscall.Execveat),
        new("Processes", "setpgid, setsid", "EPERM", "process groups and sessions are not controllable from .NET; getpgrp and getsid report the process id", Linux.Syscall.Setpgid, Linux.Syscall.Setsid),
        new("Processes", "ptrace, personality, chroot, mount, umount2, unshare, seccomp", "ENOSYS", "no process tracing, execution domains, root changes, mounts, namespaces or system-call filters in a managed process", Linux.Syscall.Ptrace, Linux.Syscall.Personality, Linux.Syscall.Chroot, Linux.Syscall.Mount, Linux.Syscall.Umount2, Linux.Syscall.Unshare, Linux.Syscall.Seccomp),
        new("Processes", "sched_setaffinity", "ENOSYS", "managed threads have no processor affinity; sched_getaffinity reports the processors .NET uses", Linux.Syscall.SchedSetaffinity),
        new("Processes", "process_vm_readv, process_vm_writev", "ENOSYS", "another process's memory is not reachable", Linux.Syscall.ProcessVmReadv, Linux.Syscall.ProcessVmWritev),
        new("Sockets", "socket, socketpair, connect, bind, listen, accept, accept4, send and receive calls, shutdown, getsockname, getpeername, setsockopt, getsockopt", "ENOSYS", "sockets are not provided in 1.0 (LC-13); a later release may implement them over .NET sockets", Linux.Syscall.Socket, Linux.Syscall.Socketpair, Linux.Syscall.Connect, Linux.Syscall.Bind, Linux.Syscall.Listen, Linux.Syscall.Accept, Linux.Syscall.Accept4, Linux.Syscall.Sendto, Linux.Syscall.Recvfrom, Linux.Syscall.Sendmsg, Linux.Syscall.Recvmsg, Linux.Syscall.Shutdown, Linux.Syscall.Getsockname, Linux.Syscall.Getpeername, Linux.Syscall.Setsockopt, Linux.Syscall.Getsockopt),
        new("File system", "link, linkat", "EPERM", ".NET cannot create hard links (LC-30)", Linux.Syscall.Link, Linux.Syscall.Linkat),
        new("File system", "mknod, mknodat", "EPERM", "no device files or named pipes; an existing FIFO is reported as a regular file (LC-30)", Linux.Syscall.Mknod, Linux.Syscall.Mknodat),
        new("File system", "setxattr, getxattr, listxattr, removexattr and their l- and f- forms", "ENOSYS", "extended attributes have no .NET interface", Linux.Syscall.Setxattr, Linux.Syscall.Lsetxattr, Linux.Syscall.Fsetxattr, Linux.Syscall.Getxattr, Linux.Syscall.Lgetxattr, Linux.Syscall.Fgetxattr, Linux.Syscall.Listxattr, Linux.Syscall.Llistxattr, Linux.Syscall.Flistxattr, Linux.Syscall.Removexattr, Linux.Syscall.Lremovexattr, Linux.Syscall.Fremovexattr),
        new("File system", "inotify_init, inotify_init1, inotify_add_watch, inotify_rm_watch", "ENOSYS", "file-system notification is not provided", Linux.Syscall.InotifyInit, Linux.Syscall.InotifyInit1, Linux.Syscall.InotifyAddWatch, Linux.Syscall.InotifyRmWatch),
        new("File system", "splice, tee, vmsplice", "ENOSYS", "pipe-to-pipe and pipe-to-memory transfers are not provided; read and write work on pipes", Linux.Syscall.Splice, Linux.Syscall.Tee, Linux.Syscall.Vmsplice),
        new("Descriptors", "epoll_create, epoll_create1, epoll_ctl, epoll_wait, epoll_pwait", "ENOSYS", "no epoll; poll, ppoll, select and pselect work on pipes and files", Linux.Syscall.EpollCreate, Linux.Syscall.EpollCreate1, Linux.Syscall.EpollCtl, Linux.Syscall.EpollWait, Linux.Syscall.EpollPwait),
        new("Descriptors", "eventfd, eventfd2, signalfd, signalfd4, timerfd_create, memfd_create, userfaultfd, io_uring_setup", "ENOSYS", "these descriptor kinds have no .NET equivalent; pipes serve as wake-up descriptors", Linux.Syscall.Eventfd, Linux.Syscall.Eventfd2, Linux.Syscall.Signalfd, Linux.Syscall.Signalfd4, Linux.Syscall.TimerfdCreate, Linux.Syscall.MemfdCreate, Linux.Syscall.Userfaultfd, Linux.Syscall.IoUringSetup),
        new("Interprocess communication", "shmget, shmat, shmdt, shmctl, semget, semop, semctl, msgget, msgsnd, msgrcv, msgctl, mq_open", "ENOSYS", "System V and POSIX message queues, semaphores and shared memory are not provided (LC-30); shared file mappings within one process work", Linux.Syscall.Shmget, Linux.Syscall.Shmat, Linux.Syscall.Shmdt, Linux.Syscall.Shmctl, Linux.Syscall.Semget, Linux.Syscall.Semop, Linux.Syscall.Semctl, Linux.Syscall.Msgget, Linux.Syscall.Msgsnd, Linux.Syscall.Msgrcv, Linux.Syscall.Msgctl, Linux.Syscall.MqOpen),
        new("Timers", "timer_create", "ENOSYS", "POSIX interval timers that deliver signals are not provided; nanosleep, clock_nanosleep and alarm-free waits work", Linux.Syscall.TimerCreate),
    ];

    /// <summary>One tab-separated line per refused service (area, calls, errno, reason), for "llvmnet --print-host-limitations".</summary>
    public static IEnumerable<string> DescribeUnavailable() => Unavailable.Select(service => $"{service.Area}\t{service.Calls}\t{service.Errno}\t{service.Reason}");

    private static readonly Dictionary<Linux.Syscall, int> unavailable = Unavailable
        .SelectMany(service => service.Numbers.Select(number => (number, errno: service.Errno == "EPERM" ? EPERM : ENOSYS)))
        .ToDictionary(pair => pair.number, pair => pair.errno);

    [CExport("__llvmnet_syscall")]
    public static long Syscall(long number, long a, long b, long c, long d, long e, long f)
    {
        try
        {
            if (unavailable.TryGetValue((Linux.Syscall)number, out int refused)) return -refused;
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
                Linux.Syscall.Sendfile => CopyRange((int)b, (nint)c, (int)a, 0, d),
                Linux.Syscall.CopyFileRange => f != 0 ? -EINVAL : CopyRange((int)a, (nint)b, (int)c, (nint)d, e),
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
                Linux.Syscall.Getpriority => 20,                       // nice value 0, offset by 20 as the kernel reports it
                Linux.Syscall.Setpriority => 0,
                Linux.Syscall.RtSigpending => PendingSignals((ulong*)a),
                Linux.Syscall.RtSigtimedwait => -EAGAIN,
                Linux.Syscall.RtSigsuspend => -EINTR,
                Linux.Syscall.Sigaltstack => 0,
                Linux.Syscall.Statfs => FileSystemStatusOf(AT_FDCWD, (nint)a, (FileSystemStatus*)b),
                Linux.Syscall.Fstatfs => FileSystemStatusOf((int)a, 0, (FileSystemStatus*)b),
                Linux.Syscall.Prctl => 0,
                Linux.Syscall.ArchPrctl => a == ARCH_SET_FS ? SetThreadPointer((nint)b) : -EINVAL,
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
            // A blocking call that pthread_cancel (SIGCANCEL) interrupted.
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
        // On Linux the runtime reports the kernel's errno for other I/O failures; macOS numbers the errors it shares with
        // Linux the same way up to ERANGE, and Windows reports a Win32 error code in the low half of the HRESULT.
        IOException io when OperatingSystem.IsLinux() && (io.HResult & 0xffff) is > 0 and < 4096 => io.HResult & 0xffff,
        IOException io when OperatingSystem.IsMacOS() && (io.HResult & 0xffff) is > 0 and < 4096 => FromDarwin(io.HResult & 0xffff),
        IOException io when OperatingSystem.IsWindows() && (io.HResult & 0xffff0000) == unchecked((int)0x80070000) => FromWin32(io.HResult & 0xffff),
        _ => EIO
    };

    private static int FromDarwin(int error) => error switch
    {
        <= ERANGE => error,
        35 => EAGAIN,
        45 => EOPNOTSUPP,
        60 => ETIMEDOUT,
        62 => ELOOP,
        63 => ENAMETOOLONG,
        66 => ENOTEMPTY,
        78 => ENOSYS,
        _ => EIO
    };

    private static int FromWin32(int error) => error switch
    {
        2 or 3 => ENOENT,                             // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
        4 => EMFILE,                                  // ERROR_TOO_MANY_OPEN_FILES
        5 or 1920 => EACCES,                          // ERROR_ACCESS_DENIED, ERROR_CANT_ACCESS_FILE
        6 => EBADF,                                   // ERROR_INVALID_HANDLE
        8 => ENOMEM,                                  // ERROR_NOT_ENOUGH_MEMORY
        17 => EXDEV,                                  // ERROR_NOT_SAME_DEVICE
        19 => EROFS,                                  // ERROR_WRITE_PROTECT
        32 or 33 => EBUSY,                            // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        39 or 112 => ENOSPC,                          // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL
        80 or 183 => EEXIST,                          // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
        87 or 123 => EINVAL,                          // ERROR_INVALID_PARAMETER, ERROR_INVALID_NAME
        109 or 232 => EPIPE,                          // ERROR_BROKEN_PIPE, ERROR_NO_DATA
        145 => ENOTEMPTY,                             // ERROR_DIR_NOT_EMPTY
        206 => ENAMETOOLONG,                          // ERROR_FILENAME_EXCED_RANGE
        267 => ENOTDIR,                               // ERROR_DIRECTORY
        1314 => EPERM,                                // ERROR_PRIVILEGE_NOT_HELD (symbolic links without the privilege)
        _ => EIO
    };

    private static string Text(nint pointer) => pointer == 0 ? throw new ArgumentException("null path") : Marshal.PtrToStringUTF8(pointer)!;

    /// <summary>The absolute host path for a path argument relative to a directory descriptor, as the *at calls take it.</summary>
    private static string Resolve(int directory, nint pathPointer)
    {
        string path = Text(pathPointer);
        if (path.Length == 0) throw new FileNotFoundException(path);
        if (path.Length >= 4096) throw new PathTooLongException();
        // Repeated slashes act as one, and a trailing slash names a directory: it is dropped, and the path must then be a
        // directory if it exists (mkdir("dir/") creates "dir"; stat("file/") is ENOTDIR). A Windows UNC path keeps its "//".
        int keep = OperatingSystem.IsWindows() && path.StartsWith("//", StringComparison.Ordinal) ? 2 : 0;
        while (path.IndexOf("//", keep, StringComparison.Ordinal) is int doubled and >= 0) path = path.Remove(doubled, 1);
        bool directoryOnly = path.Length > 1 && path.EndsWith('/');
        if (directoryOnly) path = path[..^1];
        // The paths the host layer serves itself (/dev, /etc/passwd, /proc/self/exe, zoneinfo) keep their Linux names.
        if (HostPaths.IsVirtual(path)) return path;
        path = HostPaths.ToHost(path);
        if (!Path.IsPathRooted(path))
        {
            string start = Directory.GetCurrentDirectory();
            if (directory != AT_FDCWD)
            {
                OpenFile parent = Lookup(directory) ?? throw new HostException(EBADF);
                if (parent.Kind != FileKind.Directory) throw new HostException(ENOTDIR);
                start = parent.Path!;
            }
            path = Path.Combine(start, path);
        }
        // Path.GetFullPath removes ".." textually; the kernel resolves the components before it first.
        string resolved = HostPaths.Split(path).Components.Contains("..") ? ResolveParents(path, 0) : Path.GetFullPath(path);
        if (directoryOnly && !Directory.Exists(resolved) && File.Exists(resolved)) throw new HostException(ENOTDIR);
        return resolved;
    }

    /// <summary>
    /// Resolves an absolute path containing ".." as Linux does: each directory component is looked up in order, a
    /// symbolic link among them is followed before a later "..", and a missing or non-directory component is an
    /// error. The last component itself is left unresolved, so callers can still treat a final link either way.
    /// </summary>
    private static string ResolveParents(string path, int depth)
    {
        if (depth > 40) throw new HostException(ELOOP);
        (string root, string[] components) = HostPaths.Split(path);
        string current = root;
        for (int index = 0; index < components.Length; index++)
        {
            string component = components[index];
            bool last = index == components.Length - 1;
            if (component == ".") continue;
            if (component == "..")
            {
                current = Path.GetDirectoryName(current) ?? root;
                continue;
            }
            string next = Path.Combine(current, component);
            if (last) return next;
            if (SymbolicLinkAt(next)?.LinkTarget is string linkTarget)
            {
                string target = HostPaths.ToHost(linkTarget);
                next = ResolveParents(Path.IsPathRooted(target) ? target : Path.Combine(current, target), depth + 1);
            }
            if (!Directory.Exists(next))
            {
                long error = File.Exists(next) ? -ENOTDIR : Missing(next);
                throw new HostException((int)-error);
            }
            current = next;
        }
        return current;
    }

    /// <summary>
    /// The errno for a path the .NET existence checks did not find: the host's own error when it reports one (EACCES for
    /// a path below a directory the process may not search), ENOTDIR when a parent component is not a directory, else ENOENT.
    /// </summary>
    private static long Missing(string path)
    {
        try { File.GetAttributes(path); }
        catch (UnauthorizedAccessException) { return -EACCES; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (IOException error) when (Errno(error) != EIO) { return -Errno(error); }
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
