using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

/// <summary>
/// Linux x86-64 numbers and layouts of the host interface (docs/dev/host-abi.md). musl's x86-64 bits/ headers
/// use the same values, so the host interprets every system call with them, on every operating system.
/// </summary>
internal static class Linux
{
    internal const int EPERM = 1, ENOENT = 2, ESRCH = 3, EINTR = 4, EIO = 5, ENXIO = 6, E2BIG = 7, ENOEXEC = 8, EBADF = 9,
        ECHILD = 10, EAGAIN = 11, ENOMEM = 12, EACCES = 13, EFAULT = 14, EBUSY = 16, EEXIST = 17, EXDEV = 18, ENODEV = 19,
        ENOTDIR = 20, EISDIR = 21, EINVAL = 22, ENFILE = 23, EMFILE = 24, ENOTTY = 25, EFBIG = 27, ENOSPC = 28, ESPIPE = 29,
        EROFS = 30, EMLINK = 31, EPIPE = 32, ERANGE = 34, ENAMETOOLONG = 36, ENOSYS = 38, ENOTEMPTY = 39, ELOOP = 40,
        EDEADLK = 35, EOPNOTSUPP = 95, ETIMEDOUT = 110;

    // open(2) flags.
    internal const int O_ACCMODE = 3, O_RDONLY = 0, O_WRONLY = 1, O_RDWR = 2, O_CREAT = 0x40, O_EXCL = 0x80, O_TRUNC = 0x200,
        O_APPEND = 0x400, O_NONBLOCK = 0x800, O_DIRECTORY = 0x10000, O_NOFOLLOW = 0x20000, O_CLOEXEC = 0x80000, O_PATH = 0x200000,
        O_TMPFILE = 0x400000;

    // *at(2) arguments.
    internal const int AT_FDCWD = -100, AT_SYMLINK_NOFOLLOW = 0x100, AT_REMOVEDIR = 0x200, AT_EMPTY_PATH = 0x1000;

    // fcntl(2).
    internal const int F_DUPFD = 0, F_GETFD = 1, F_SETFD = 2, F_GETFL = 3, F_SETFL = 4, F_GETLK = 5, F_SETLK = 6, F_SETLKW = 7,
        F_OFD_GETLK = 36, F_OFD_SETLK = 37, F_OFD_SETLKW = 38, F_DUPFD_CLOEXEC = 1030, FD_CLOEXEC = 1;

    // ioctl(2) requests.
    internal const long TCGETS = 0x5401, TIOCGWINSZ = 0x5413, FIONREAD = 0x541B, FIONBIO = 0x5421;

    // st_mode file types and d_type values.
    internal const uint S_IFIFO = 0x1000, S_IFCHR = 0x2000, S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFLNK = 0xA000;
    internal const byte DT_FIFO = 1, DT_CHR = 2, DT_DIR = 4, DT_REG = 8, DT_LNK = 10;

    // mmap(2), mremap(2), msync(2), madvise(2).
    internal const int PROT_WRITE = 2, MAP_SHARED = 1, MAP_PRIVATE = 2, MAP_FIXED = 0x10, MAP_ANONYMOUS = 0x20,
        MAP_FIXED_NOREPLACE = 0x100000, MREMAP_MAYMOVE = 1, MADV_DONTNEED = 4;
    internal const long PageSize = 4096;

    // futex(2), clone(2).
    internal const int FUTEX_WAIT = 0, FUTEX_WAKE = 1, FUTEX_REQUEUE = 3, FUTEX_CMP_REQUEUE = 4, FUTEX_LOCK_PI = 6,
        FUTEX_UNLOCK_PI = 7, FUTEX_TRYLOCK_PI = 8, FUTEX_WAIT_BITSET = 9, FUTEX_WAKE_BITSET = 10, FUTEX_CMD_MASK = 127,
        FUTEX_CLOCK_REALTIME = 256;
    internal const int FUTEX_TID_MASK = 0x3fffffff, FUTEX_OWNER_DIED = 0x40000000, FUTEX_WAITERS = unchecked((int)0x80000000);
    internal const int CLONE_SETTLS = 0x80000, CLONE_PARENT_SETTID = 0x100000, CLONE_CHILD_CLEARTID = 0x200000;
    internal const long ARCH_SET_FS = 0x1002;

    // Signals.
    internal const int SIGHUP = 1, SIGINT = 2, SIGQUIT = 3, SIGILL = 4, SIGTRAP = 5, SIGABRT = 6, SIGBUS = 7, SIGFPE = 8,
        SIGKILL = 9, SIGSEGV = 11, SIGPIPE = 13, SIGTERM = 15, SIGCHLD = 17, SIGCONT = 18, SIGSTOP = 19, SIGURG = 23,
        SIGWINCH = 28, SIGCANCEL = 33, NSIG = 64;
    internal const nint SIG_DFL = 0, SIG_IGN = 1;
    internal const int SIG_BLOCK = 0, SIG_UNBLOCK = 1, SIG_SETMASK = 2;
    internal const ulong SA_SIGINFO = 4, SA_RESETHAND = 0x80000000;
    internal const int SI_TKILL = -6;

    // Clocks.
    internal const int CLOCK_REALTIME = 0, CLOCK_MONOTONIC = 1, CLOCK_PROCESS_CPUTIME_ID = 2, CLOCK_THREAD_CPUTIME_ID = 3,
        CLOCK_MONOTONIC_RAW = 4, CLOCK_REALTIME_COARSE = 5, CLOCK_MONOTONIC_COARSE = 6, CLOCK_BOOTTIME = 7, TIMER_ABSTIME = 1;
    internal const long UTIME_NOW = (1L << 30) - 1, UTIME_OMIT = (1L << 30) - 2;

    // poll(2) events.
    internal const short POLLIN = 1, POLLOUT = 4, POLLERR = 8, POLLHUP = 0x10, POLLNVAL = 0x20, POLLRDNORM = 0x40, POLLWRNORM = 0x100;

    internal const int WNOHANG = 1;
    internal const int RLIMIT_DATA = 2, RLIMIT_STACK = 3, RLIMIT_NOFILE = 7, RLIMIT_AS = 9, RLIMIT_COUNT = 16;
    internal const ulong RLIM_INFINITY = ulong.MaxValue;
    internal const int R_OK = 4, W_OK = 2, X_OK = 1;

    // dlopen(3) flags and dlsym(3) pseudo-handles, as musl's dlfcn.h defines them.
    internal const int RTLD_NOLOAD = 4, RTLD_GLOBAL = 256;
    internal const nint RTLD_DEFAULT = 0, RTLD_NEXT = -1;

    /// <summary>The identity every portable program sees on every host: one user and group, number 1000.</summary>
    internal const int UserId = 1000, GroupId = 1000;

    /// <summary>
    /// System call numbers the host implements, and the ones it refuses by name (<see cref="HostSyscalls.Unavailable"/>);
    /// any other number returns -ENOSYS.
    /// </summary>
    internal enum Syscall : long
    {
        Read = 0, Write = 1, Open = 2, Close = 3, Stat = 4, Fstat = 5, Lstat = 6, Poll = 7, Lseek = 8, Mmap = 9, Mprotect = 10,
        Munmap = 11, Brk = 12, RtSigaction = 13, RtSigprocmask = 14, Ioctl = 16, Pread64 = 17, Pwrite64 = 18, Readv = 19,
        Writev = 20, Access = 21, Pipe = 22, Select = 23, SchedYield = 24, Mremap = 25, Msync = 26, Mincore = 27, Madvise = 28,
        Dup = 32, Dup2 = 33, Nanosleep = 35, Getpid = 39, Sendfile = 40, Fork = 57, Vfork = 58, Execve = 59, Exit = 60, Wait4 = 61, Kill = 62,
        Uname = 63, Fcntl = 72, Flock = 73, Fsync = 74, Fdatasync = 75, Truncate = 76, Ftruncate = 77, Getcwd = 79, Chdir = 80,
        Fchdir = 81, Rename = 82, Mkdir = 83, Rmdir = 84, Link = 86, Unlink = 87, Symlink = 88, Readlink = 89, Chmod = 90,
        Fchmod = 91, Chown = 92, Fchown = 93, Lchown = 94, Umask = 95, Gettimeofday = 96, Getrusage = 98, Sysinfo = 99,
        Times = 100, Getuid = 102, Getgid = 104, Setuid = 105, Setgid = 106, Geteuid = 107, Getegid = 108, Setpgid = 109,
        Getppid = 110, Getpgrp = 111, Setsid = 112, Getgroups = 115, Getpgid = 121, Getsid = 124, RtSigpending = 127,
        RtSigtimedwait = 128, RtSigsuspend = 130, Sigaltstack = 131, Mknod = 133, Statfs = 137, Fstatfs = 138,
        Getpriority = 140, Setpriority = 141, Mlock = 149, Munlock = 150, Mlockall = 151, Munlockall = 152, Prctl = 157,
        ArchPrctl = 158, Setrlimit = 160, Sync = 162, Gettid = 186, Tkill = 200, Time = 201, Futex = 202,
        SchedGetaffinity = 204, SetThreadArea = 205, Getdents64 = 217, SetTidAddress = 218, Fadvise64 = 221,
        ClockGettime = 228, ClockGetres = 229, ClockNanosleep = 230, ExitGroup = 231, Tgkill = 234, Utimes = 235,
        Openat = 257, Mkdirat = 258, Mknodat = 259, Fchownat = 260, Newfstatat = 262, Unlinkat = 263, Renameat = 264,
        Linkat = 265, Symlinkat = 266, Readlinkat = 267, Fchmodat = 268, Faccessat = 269, Pselect6 = 270, Ppoll = 271,
        SetRobustList = 273, GetRobustList = 274, Utimensat = 280, Fallocate = 285, Dup3 = 292, Pipe2 = 293, Preadv = 295, Pwritev = 296, Prlimit64 = 302,
        Syncfs = 306, Renameat2 = 316, Getrandom = 318, Membarrier = 324, CopyFileRange = 326, Preadv2 = 327, Pwritev2 = 328, Faccessat2 = 439,
        // Refused by name: services .NET cannot provide (HostSyscalls.Unavailable gives the errno and the reason).
        Shmget = 29, Shmat = 30, Shmctl = 31, Socket = 41, Connect = 42, Accept = 43, Sendto = 44, Recvfrom = 45, Sendmsg = 46,
        Recvmsg = 47, Shutdown = 48, Bind = 49, Listen = 50, Getsockname = 51, Getpeername = 52, Socketpair = 53, Setsockopt = 54,
        Getsockopt = 55, Clone = 56, Semget = 64, Semop = 65, Semctl = 66, Shmdt = 67, Msgget = 68, Msgsnd = 69, Msgrcv = 70, Msgctl = 71,
        Ptrace = 101, Personality = 135, Chroot = 161, Mount = 165, Umount2 = 166, Setxattr = 188, Lsetxattr = 189, Fsetxattr = 190,
        Getxattr = 191, Lgetxattr = 192, Fgetxattr = 193, Listxattr = 194, Llistxattr = 195, Flistxattr = 196, Removexattr = 197,
        Lremovexattr = 198, Fremovexattr = 199, SchedSetaffinity = 203, EpollCreate = 213, TimerCreate = 222, EpollWait = 232,
        EpollCtl = 233, MqOpen = 240, InotifyInit = 253, InotifyAddWatch = 254, InotifyRmWatch = 255, Unshare = 272, Splice = 275,
        Tee = 276, Vmsplice = 278, EpollPwait = 281, Signalfd = 282, TimerfdCreate = 283, Eventfd = 284, Accept4 = 288, Signalfd4 = 289,
        Eventfd2 = 290, EpollCreate1 = 291, InotifyInit1 = 294, ProcessVmReadv = 310, ProcessVmWritev = 311, Seccomp = 317,
        MemfdCreate = 319, Execveat = 322, Userfaultfd = 323, IoUringSetup = 425, Clone3 = 435,
    }

    /// <summary>struct stat as the x86-64 kernel fills it (144 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    internal struct Stat
    {
        [FieldOffset(0)] internal ulong Device;
        [FieldOffset(8)] internal ulong Inode;
        [FieldOffset(16)] internal ulong Links;
        [FieldOffset(24)] internal uint Mode;
        [FieldOffset(28)] internal uint User;
        [FieldOffset(32)] internal uint Group;
        [FieldOffset(40)] internal ulong SpecialDevice;
        [FieldOffset(48)] internal long Size;
        [FieldOffset(56)] internal long BlockSize;
        [FieldOffset(64)] internal long Blocks;
        [FieldOffset(72)] internal Timespec Access;
        [FieldOffset(88)] internal Timespec Modification;
        [FieldOffset(104)] internal Timespec Change;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Timespec
    {
        internal long Seconds;
        internal long Nanoseconds;

        internal static Timespec From(TimeSpan sinceEpoch) =>
            new() { Seconds = Math.DivRem(sinceEpoch.Ticks, TimeSpan.TicksPerSecond, out long remainder), Nanoseconds = remainder * 100 };

        internal static Timespec From(DateTime utc) => From(utc - DateTime.UnixEpoch);

        /// <summary>The duration, or null when the nanoseconds are out of range or the value is negative (EINVAL).</summary>
        internal readonly TimeSpan? Duration =>
            Seconds < 0 || Nanoseconds is < 0 or >= 1_000_000_000 ? null : TimeSpan.FromTicks(Seconds * TimeSpan.TicksPerSecond + Nanoseconds / 100);
    }

    /// <summary>struct statfs on x86-64 (120 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 120)]
    internal struct FileSystemStatus
    {
        internal long Type, BlockSize, Blocks, FreeBlocks, AvailableBlocks, Files, FreeFiles, Identifier, NameLength, FragmentSize, Flags;
    }

    /// <summary>struct k_sigaction as musl passes it to rt_sigaction.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SignalAction
    {
        internal nint Handler;
        internal ulong Flags;
        internal nint Restorer;
        internal ulong Mask;
    }

    /// <summary>Dl_info, which dladdr fills.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DlInfo
    {
        internal nint FileName;
        internal nint Base;
        internal nint SymbolName;
        internal nint SymbolAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollDescriptor
    {
        internal int Descriptor;
        internal short Events;
        internal short ReturnedEvents;
    }
}
