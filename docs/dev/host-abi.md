# Host interface for musl (spike state)

With `--portable-libc`, translated programs reach the host only through the functions below, implemented in C# by `src/LlvmNet.Runtime/HostSyscalls.cs` (plan items LC-04 to LC-11). This page describes the interface as the M2 spike built it; it becomes a versioned contract when the musl sysroot becomes the default (LC-26).

| Symbol | C signature | Purpose |
| --- | --- | --- |
| `__llvmnet_syscall` | `long (long number, long a1, ..., long a6)` | Every musl system call. Numbers, argument layouts, structure layouts and `-errno` results follow Linux x86-64. Unimplemented calls return `-ENOSYS`. |
| `__llvmnet_get_tp` | `uintptr_t (void)` | The calling thread's thread pointer, set by `set_thread_area` (205) or `arch_prctl(ARCH_SET_FS)`. |
| `__llvmnet_clone` | `int (int (*)(void *), void *, int flags, int *ptid, void *tls, int *ctid)` | Thread creation for musl's `__clone`, on a managed thread; honors `CLONE_SETTLS`, `CLONE_PARENT_SETTID` and `CLONE_CHILD_CLEARTID`. |
| `__llvmnet_run_constructors` | `void (void)` | Supplied by the compiler: runs `llvm.global_ctors` and registers `llvm.global_dtors` with musl's `atexit`. |

`setjmp` and `longjmp` are handled by the compiler and `NonLocalJumps`. C++ exception handling stays in the managed bridge (`Cxx.cs`) because the CLR unwinds.

## Behavior that differs from a Linux kernel

- Signals are synchronous: `kill`, `tkill` and `tgkill` aimed at this process run the handler on the calling thread, ignore the signal, or end the process with status 128+signal. Nothing is delivered asynchronously.
- `brk` reports no heap, so musl's allocator uses `mmap`. `MAP_FIXED` is rejected, file mappings are private copies, and partial `munmap` keeps the memory until the whole region is unmapped.
- `/dev/urandom`, `/dev/random`, `/dev/null`, `/dev/zero`, `/dev/stdin`, `/dev/stdout`, `/dev/stderr` and `/dev/tty` are provided by the host on every operating system.
- There are no symbolic links, `fork`, `exec`, pipes or `poll` yet; user and group ids are reported as 1000 (LC-12, LC-28).
- The process start vector carries `AT_PAGESZ`, `AT_RANDOM` and equal real and effective ids; there are no ELF program headers, so musl's TLS setup uses its built-in thread block.
