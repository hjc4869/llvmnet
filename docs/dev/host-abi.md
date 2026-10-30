# Host interface of the portable ABI

Portable programs (`dotnet64-v2`) contain musl, libc++ and flang-rt compiled to CIL. They reach the operating system only through the host interface on this page, implemented in C# in `src/LlvmNet.Runtime/Host/` (plan items LC-04 to LC-12). This page is the contract between the sysroot (`sysroot/dotnet64-v2`, built from `ports/musl`) and `LlvmNet.Runtime`.

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
| `__llvmnet_spawn` | `int (pid_t *pid, const char *path, int search, char *const argv[], char *const envp[], const int streams[3], const char *directory)` | musl's `posix_spawn` (`src/process/llvmnet/posix_spawn.c`): starts a host process. `streams` names the descriptors that become the child's standard input, output and error after the file actions, or -1 for closed; `search` asks for a `PATH` search. Returns 0 or an errno value. |
| `__llvmnet_run_constructors` | `void (void)` | Supplied by the compiler in every image: runs `llvm.global_ctors` and registers `llvm.global_dtors` with musl's `atexit`. |

`setjmp` and `longjmp` are handled by the compiler and `NonLocalJumps`. C++ exceptions stay in the managed bridge (`Cxx.cs`) because the CLR unwinds the stack.

The entry points and their semantics are versioned with the ABI tag. Implementing an additional system call is compatible. Changing an entry point's signature, or the meaning of an implemented call in a way musl depends on, requires a new ABI tag (ADR-09).

## Calls into translated code

The CLR calls through a function pointer by the call site's CIL signature and does not check the target (BE-18), so every place where the runtime calls a C function pointer it was handed is listed here with the signature it assumes (plan item LC-18). Calls between translated functions, such as musl calling `atexit` handlers or `qsort` comparators and `__libc_start_main` calling `main` through the compiler's `__llvmnet_main3` adapter, are compiled code and follow [calling-convention.md](calling-convention.md).

| Site | Mode | Signature called | Pointer comes from | Mismatch |
| --- | --- | --- | --- | --- |
| `HostThreads.Clone` | portable | `int (void *)` | musl's `pthread_create` and `thrd_create` (`start`, `start_c11`) | Cannot occur: only musl's own functions are passed. |
| `HostProcess` signal delivery | portable | `void (int)`, or `void (int, siginfo_t *, void *)` with `SA_SIGINFO` | the program, through `sigaction` and `signal` | A handler whose type does not match `SA_SIGINFO` is undefined in C too; native x86-64 tolerates it and the CLR does not. `--check-indirect-calls` does not cover this site. |
| `Cxx.Terminate` | both | `void (void)` | `std::set_terminate` | The C++ type `terminate_handler` fixes it. |
| `Cxx` catch matching | portable | `int (const type_info *, const type_info *, void **)` | `__llvmnet_cxx_can_catch` in libc++abi, registered by the image initializer | Cannot occur: llvmnet's own function. |
| `Cxx` exception destructors | system | `void (void *)` | `__cxa_throw` | The Itanium ABI fixes it. |
| `ExitHandlers` (`atexit`, `__cxa_atexit`, `__cxa_finalize`) | system | `void (void)`, `void (void *)` | the program; C++ static destructors | C's and the Itanium ABI's types fix it. |
| `ThreadStorage` (`__cxa_thread_atexit`) | system | `void (void *)` | C++ `thread_local` destructors | The Itanium ABI fixes it. |
| `SystemThreads.Start` | system | `void *(void *)`, called as a native function | `pthread_create` | Native code: the x86-64 convention applies. |

In system mode a callback may be a native function or a translated one. `SystemAbi.ResolveCallback` maps the native entry points the runtime created for translated functions back to them, and a pointer it does not know is called as native code. In portable mode every pointer is a translated function, because portable images contain no native code.

## System calls

| Area | Implemented | Not available |
| --- | --- | --- |
| Descriptors (LC-05) | `read`, `write`, `readv`, `writev`, `pread64`, `pwrite64`, `preadv`, `pwritev`, `sendfile` and `copy_file_range` (from regular files, by copying), `open`, `openat`, `close`, `lseek` (including `SEEK_DATA`, `SEEK_HOLE`), `dup`, `dup2`, `dup3`, `fcntl` (`F_DUPFD`, `F_DUPFD_CLOEXEC`, `F_GETFD`, `F_SETFD`, `F_GETFL`, `F_SETFL`, locks), `flock`, `fsync`, `fdatasync`, `ftruncate`, `fallocate` (mode 0), `ioctl` (`TIOCGWINSZ`, `TCGETS`, `TCSETS*`, `FIONREAD`, `FIONBIO`), `pipe`, `pipe2`, `poll`, `ppoll`, `select`, `pselect6` | sockets (`-ENOSYS`, LC-13), `epoll`, `eventfd`, `inotify` |
| File system (LC-06) | `stat`, `fstat` (through the open handle, so unlinked files work), `lstat`, `newfstatat`, `access`, `faccessat`, `faccessat2`, `getdents64`, `mkdir`, `mkdirat`, `rmdir`, `unlink`, `unlinkat`, `rename`, `renameat`, `renameat2` (`RENAME_NOREPLACE`), `symlink`, `symlinkat`, `readlink`, `readlinkat`, `chmod`, `fchmod`, `fchmodat`, `chown` family, `truncate`, `getcwd`, `chdir`, `fchdir`, `utimes`, `utimensat`, `umask`, `statfs`, `fstatfs` | hard links (`link`, `linkat`: `-EPERM`), device files and named pipes (`mknod`: `-EPERM`) |
| Memory (LC-07) | `mmap` (anonymous, private and shared file mappings), `munmap` (also partial), `mremap`, `msync`, `madvise` (`MADV_DONTNEED` restores private pages), `mprotect` and `mlock` family as no-ops, `brk` (reports no heap) | memory protection, `MAP_FIXED` outside an existing mapping |
| Processes (LC-08) | `exit`, `exit_group`, `getpid`, `getppid`, `gettid`, `kill`, `tkill`, `tgkill` (to this process), `rt_sigaction`, `rt_sigprocmask`, `rt_sigpending`, `sigaltstack`, `wait4`, `uname`, `prlimit64`, `getrusage`, `sysinfo`, `times`, `sched_getaffinity`, `sched_yield`, `prctl`; child processes through `__llvmnet_spawn` | `fork`, `vfork`, `execve` (`-ENOSYS`), process groups and sessions (`-EPERM`) |
| Identity (LC-12) | `getuid`, `geteuid`, `getgid`, `getegid` (all 1000), `getgroups`, `setuid` and `setgid` to the same ids | other ids (`-EPERM`) |
| Time (LC-09) | `clock_gettime`, `clock_getres`, `clock_nanosleep`, `nanosleep`, `gettimeofday`, `time` | |
| Threads (LC-02) | `futex` (wait, wake, bitset variants, requeue as wake-all), `set_thread_area`, `set_tid_address`, `membarrier`, `exit` of a thread | |
| Entropy (LC-11) | `getrandom` | |

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
- **CPU clocks.** `CLOCK_THREAD_CPUTIME_ID` reports the process CPU time, because .NET has no per-thread CPU clock.
- **Time zones** come from `TZ` and the zoneinfo files musl reads (`/etc/localtime`, `/usr/share/zoneinfo`), which Linux and macOS hosts have. A host without them, such as Windows, gets UTC unless `TZ` holds a POSIX rule (LC-28).
- **The process start vector** carries `AT_PAGESZ`, `AT_RANDOM` and equal real and effective ids. There are no ELF program headers, so musl's TLS setup uses its built-in thread block.

The conformance test `tests/host.sh` (LC-14) runs `tests/c/host-errors.c` natively and in portable mode and requires identical output for every call above that has a native equivalent.
