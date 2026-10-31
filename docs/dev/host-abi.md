# Host interface of the portable ABI

Portable programs (`dotnet64-v2`) contain musl, libc++ and flang-rt compiled to CIL. They reach the operating system only through the host interface on this page, implemented in C# in `src/LlvmNet.Runtime/Host/`. This page is the contract between the sysroot (`sysroot/dotnet64-v2`, built from [ports/musl](../../ports/musl/README.md)) and `LlvmNet.Runtime`.

## Rules

- **Linux system-call semantics.** The host implements a subset of the Linux x86-64 system-call interface: the same numbers, argument order, structure layouts and flag values as musl's `arch/x86_64` headers. musl therefore runs unmodified, and the behavior is the same on every .NET host operating system.
- **Results.** Every call returns a non-negative result or `-errno`, as the kernel does; musl turns that into `errno`. A number the host does not implement returns `-ENOSYS`.
- **Constants.** Numbers, flags and layouts are named in `Host/Linux.cs`; no other C# file uses a raw Linux constant.
- **Errors from .NET.** `HostSyscalls.Errno` is the single mapping from .NET exceptions to errno values. On Linux, an `IOException` carrying the kernel's errno keeps it; elsewhere unknown I/O errors become `EIO`.
- **Imports are scalar.** Every entry point takes and returns integers and pointers only, so its CIL signature cannot change with the C type system.

## Entry points

| Symbol | C signature | Purpose |
| --- | --- | --- |
| `__llvmnet_syscall` | `long (long number, long a1, ..., long a6)` | Every musl system call (`arch/llvmnet/syscall_arch.h`). |
| `__llvmnet_get_tp` | `uintptr_t (void)` | The calling thread's thread pointer, set by `set_thread_area` or `arch_prctl(ARCH_SET_FS)` (`arch/llvmnet/pthread_arch.h`). |
| `__llvmnet_clone` | `int (int (*)(void *), void *, int flags, int *ptid, void *tls, int *ctid)` | musl's `__clone` for `pthread_create`, on a managed thread; honors `CLONE_SETTLS`, `CLONE_PARENT_SETTID` and `CLONE_CHILD_CLEARTID`. |
| `__llvmnet_spawn` | `int (pid_t *pid, const char *path, int search, char *const argv[], char *const envp[], const int streams[3], const char *directory)` | musl's `posix_spawn` (`ports/musl/src/process/llvmnet/posix_spawn.c`): starts a host process. `streams` names the descriptors that become the child's standard input, output and error after the file actions, or -1 for closed; `search` asks for a `PATH` search. Returns 0 or an errno value. |
| `__llvmnet_libc_main` | `int (int argc, char **argv, char **envp, int (*main)(int, char **, char **))` | Defined by the sysroot's `crt1.o` (`ports/musl/crt/llvmnet-start.c`). The image's entry method calls it with the process arguments and environment and the compiler's `__llvmnet_main3` adapter for `main`; it initializes musl, runs the constructors, calls `main` and passes its result to `exit`. |
| `__llvmnet_run_constructors` | `void (void)` | Supplied by the compiler in every image: runs `llvm.global_ctors` and registers each `llvm.global_dtors` entry through `__llvmnet_register_finalizer`. |
| `__llvmnet_register_finalizer` | `int (void (*)(void))` | Defined by `crt1.o`: registers a destructor with musl's `atexit`, so destructors run before stdio is flushed. |

`setjmp` and `longjmp` are handled by the compiler and `NonLocalJumps`. C++ exceptions are handled by the managed bridge ([cxx-eh-bridge.md](cxx-eh-bridge.md)) because the CLR unwinds the stack.

The entry points and their semantics are versioned with the ABI tag ([versioning.md](versioning.md#abi-tags)). Implementing an additional system call is compatible. Changing an entry point's signature, or the meaning of an implemented call in a way musl depends on, requires a new ABI tag.

## Calls into translated code

The CLR calls through a function pointer by the call site's CIL signature and does not check the target ([calling-convention.md](calling-convention.md#indirect-calls)), so every place where the runtime calls a C function pointer it was handed is listed here with the signature it assumes. Calls between translated functions, such as musl calling `atexit` handlers or `qsort` comparators and `__llvmnet_libc_main` calling `main` through the `__llvmnet_main3` adapter, are compiled code and follow the same rules.

| Site | Mode | Signature called | Pointer comes from | Mismatch |
| --- | --- | --- | --- | --- |
| `HostThreads.Clone` | portable | `int (void *)` | musl's `pthread_create` and `thrd_create` (`start`, `start_c11`) | Cannot occur: only musl's own functions are passed. |
| `HostProcess` signal delivery | portable | `void (int)`, or `void (int, siginfo_t *, void *)` with `SA_SIGINFO` | the program, through `sigaction` and `signal` | A handler whose type does not match `SA_SIGINFO` is undefined in C too; native x86-64 tolerates it and the CLR does not. `--check-indirect-calls` does not cover this site. |
| `Cxx.Terminate` | both | `void (void)` | `std::set_terminate` | The C++ type `terminate_handler` fixes it. |
| `Cxx` catch matching | portable | `int (const type_info *, const type_info *, void **)` | `__llvmnet_cxx_can_catch` in libc++abi, registered by the image initializer | Cannot occur: llvmnet's own function. |
| `Cxx` exception destructors | both | `void (void *)` | `__cxa_throw` | The Itanium ABI fixes it. |
| `ExitHandlers` (`atexit`, `__cxa_atexit`, `__cxa_finalize`) | system | `void (void)`, `void (void *)` | the program; C++ static destructors | C's and the Itanium ABI's types fix it. |
| `ThreadStorage` (`__cxa_thread_atexit`) | system | `void (void *)` | C++ `thread_local` destructors | The Itanium ABI fixes it. |
| `SystemThreads.Start` | system | `void *(void *)`, called as a native function | `pthread_create` | Native code: the x86-64 convention applies. |

In system mode a callback may be a native function or a translated one. `SystemAbi.ResolveCallback` maps the native entry points the runtime created for translated functions back to them, and a pointer it does not know is called as native code. In portable mode every pointer is a translated function, because portable images contain no native code.

## System calls

| Area | Implemented | Not available |
| --- | --- | --- |
| Descriptors | `read`, `write`, `readv`, `writev`, `pread64`, `pwrite64`, `preadv`, `pwritev`, `sendfile` and `copy_file_range` (from regular files, by copying), `open`, `openat`, `close`, `lseek` (including `SEEK_DATA`, `SEEK_HOLE`), `dup`, `dup2`, `dup3`, `fcntl` (`F_DUPFD`, `F_DUPFD_CLOEXEC`, `F_GETFD`, `F_SETFD`, `F_GETFL`, `F_SETFL`, locks), `flock`, `fsync`, `fdatasync`, `sync`, `syncfs`, `fadvise64` (no effect), `ftruncate`, `fallocate` (mode 0), `ioctl` (`TIOCGWINSZ`, `TCGETS`, `TCSETS*`, `FIONREAD`, `FIONBIO`), `pipe`, `pipe2`, `poll`, `ppoll`, `select`, `pselect6` | sockets (`-ENOSYS`), `epoll`, `eventfd`, `inotify` |
| File system | `stat`, `fstat` (through the open handle, so unlinked files work), `lstat`, `newfstatat`, `access`, `faccessat`, `faccessat2`, `getdents64`, `mkdir`, `mkdirat`, `rmdir`, `unlink`, `unlinkat`, `rename`, `renameat`, `renameat2` (`RENAME_NOREPLACE`), `symlink`, `symlinkat`, `readlink`, `readlinkat`, `chmod`, `fchmod`, `fchmodat`, `chown` family, `truncate`, `getcwd`, `chdir`, `fchdir`, `utimes`, `utimensat`, `umask`, `statfs`, `fstatfs` | hard links (`link`, `linkat`: `-EPERM`), device files and named pipes (`mknod`: `-EPERM`) (LC-30) |
| Memory | `mmap` (anonymous, private and shared file mappings), `munmap` (also partial), `mremap`, `msync`, `madvise` (`MADV_DONTNEED` restores private pages), `mprotect` and `mlock` family as no-ops, `brk` (reports no heap) | memory protection, `MAP_FIXED` outside an existing mapping |
| Processes | `exit`, `exit_group`, `getpid`, `getppid`, `gettid`, `kill`, `tkill`, `tgkill` (to this process), `rt_sigaction`, `rt_sigprocmask`, `rt_sigpending`, `wait4`, `uname`, `prlimit64`, `setrlimit`, `getrusage`, `sysinfo`, `times`, `sched_getaffinity`, `sched_yield`, `getpriority`, `setpriority` (no effect), `prctl`; `getpgrp`, `getpgid` and `getsid` return the process id; child processes through `__llvmnet_spawn` | `fork`, `vfork`, `execve` (`-ENOSYS`), `setpgid`, `setsid` (`-EPERM`), alternate signal stacks (`sigaltstack` succeeds without effect), `rt_sigsuspend` (returns `-EINTR` at once) and `rt_sigtimedwait` (`-EAGAIN`) |
| Identity | `getuid`, `geteuid`, `getgid`, `getegid` (all 1000), `getgroups`, `setuid` and `setgid` to the same ids | other ids (`-EPERM`) |
| Time | `clock_gettime`, `clock_getres`, `clock_nanosleep`, `nanosleep`, `gettimeofday`, `time` | |
| Threads | `futex` (wait, wake, bitset variants, requeue as wake-all), `set_thread_area`, `set_tid_address`, `set_robust_list`, `get_robust_list`, `membarrier`, `exit` of a thread | |
| Entropy | `getrandom` | |

## Behavior that differs from a Linux kernel

- **Signals** raised inside the process (`raise`, `kill`, `abort`, `SIGPIPE` from a pipe write) are delivered synchronously on the raising thread, honoring the thread's signal mask, `SA_RESETHAND`, `SA_NODEFER` and `SA_SIGINFO` (with a minimal `siginfo_t` and an empty `ucontext_t`). `SIGINT`, `SIGTERM`, `SIGHUP` and `SIGQUIT` sent from outside reach an installed handler on a .NET signal thread, through `PosixSignalRegistration`. Faults such as `SIGSEGV` and `SIGFPE` are not delivered; the CLR reports them. A default action that ends the process prints `Aborted` or `Terminated by signal N` and exits with 128 + N.
- **Child processes** are host processes. Only the standard input, output and error descriptors are passed on; pipes and files are connected by copying threads, and spawn attributes are ignored. `system` and `popen` need `/bin/sh` on the host.
- **Memory mappings** of files are copies: shared writable mappings reach the file at `msync` and `munmap`, and other processes do not see the pages. There is no memory protection.
- **Identity.** The process runs as user and group 1000 on every host, owns every file it can see (`access` and `stat` use the owner permission bits), and reads synthetic `/etc/passwd` and `/etc/group` files naming the current .NET user.
- **Devices.** `/dev/null`, `/dev/zero`, `/dev/urandom`, `/dev/random`, `/dev/stdin`, `/dev/stdout`, `/dev/stderr` and `/dev/tty` are provided by the host on every operating system. `readlink("/proc/self/exe")` names the host executable.
- **Metadata.** Inode numbers are hashes of the full path, every file is on device 1 with one link, and the change time is the modification time. `statfs` reports the .NET drive's sizes and file-system type 0.
- **File types.** .NET can create and describe regular files, directories and symbolic links only: hard links (`link`), named pipes and device files (`mknod`) and Unix sockets are not available, and an existing FIFO or socket is reported as a regular file.
- **Paths and permissions.** A path containing `..` is resolved component by component, following symbolic links before the `..`, as the kernel does. A path the operating system refuses (for example below a directory the process may not search) fails with the operating system's error, usually `EACCES`.
- **Descriptor-relative calls.** `openat`, `unlinkat`, `fstatat` and the other `*at` calls resolve their path against the directory's path, because .NET has no descriptor-relative file API. They are therefore not safe against a directory being replaced between calls, which libc++'s `remove_all` relies on to defend against symbolic-link races (libc++ test `fs.op.remove_all/toctou.pass.cpp`).
- **Processors.** `sched_getaffinity` reports the first `Environment.ProcessorCount` processors, so `sysconf(_SC_NPROCESSORS_ONLN)` and OpenMP's default team size honor cgroup limits, affinity and `DOTNET_PROCESSOR_COUNT`; `sched_setaffinity` is not available, because managed threads have no affinity.
- **CPU clocks.** `CLOCK_THREAD_CPUTIME_ID` reports the process CPU time, because .NET has no per-thread CPU clock.
- **Time zones** come from `TZ` and the zoneinfo files musl reads (`/etc/localtime`, then `/usr/share/zoneinfo`, `/share/zoneinfo` and `/etc/zoneinfo`), which Linux and macOS hosts have. Where the host has no such file (Windows, or a container without tzdata), the host layer serves a TZif image generated from .NET's `TimeZoneInfo` instead: `/etc/localtime` from the local zone, a zoneinfo path from `FindSystemTimeZoneById` with the name musl asks for, which on Windows resolves both IANA and Windows zone ids. The image holds every transition .NET knows between 1900 and 2037, each with its offset and daylight flag, and a POSIX rule footer for later years. Two differences from tzdata: abbreviations are numeric (`+01`, `-0330`), because .NET exposes none, and offsets are whole minutes, so the local-mean-time offsets with seconds that tzdata has for years before standard time (America/St_Johns until 1935, Asia/Tehran until 1946) are rounded. `LLVMNET_ZONEINFO=managed` serves generated images even where the host has files, which `tests/zoneinfo.sh` uses to compare the two; `LLVMNET_ZONEINFO=host` never generates one.
- **The process start vector** carries `AT_PAGESZ`, `AT_RANDOM` and equal real and effective ids. There are no ELF program headers, so musl's TLS setup uses its built-in thread block.

The conformance test `tests/host.sh` runs `tests/c/host-errors.c` natively and in portable mode and requires identical output for every call above that has a native equivalent.
