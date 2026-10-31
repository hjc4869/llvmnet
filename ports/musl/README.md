# musl port for the dotnet64-v2 ABI

This directory holds the llvmnet port of musl 1.2.5. `scripts/build-portable-libc.sh` copies `arch/x86_64` from the pinned upstream source to `arch/llvmnet`, overlays `arch/llvmnet` and `src` from here, installs `include` over musl's public headers, and compiles musl's generic C sources with llvmnet into LLVM bitcode. A file `src/<dir>/llvmnet/<name>.c` here takes the place of upstream's `src/<dir>/<name>.c`. `scripts/build-sysroot.sh` runs the build and installs the result in `sysroot/dotnet64-v2`. The host functions the port calls are specified in [docs/dev/host-abi.md](../../docs/dev/host-abi.md).

- `arch/llvmnet/syscall_arch.h` sends every system call to `__llvmnet_syscall`, implemented in C# by `src/LlvmNet.Runtime/Host/HostSyscalls.cs`. The host implements Linux x86-64 system-call numbers and semantics over .NET APIs, so it behaves the same on every .NET host.
- `arch/llvmnet/atomic_arch.h` implements musl's atomics with compiler builtins, which llvmnet lowers to `System.Threading.Interlocked` operations.
- `arch/llvmnet/pthread_arch.h` reads the thread pointer from a per-thread host slot (`__llvmnet_get_tp`).
- `arch/llvmnet/crt_arch.h` is empty, because programs have no ELF entry point, and `reloc.h` defines only what `src/thread/__unmapself.c` needs, because the dynamic linker is not built.
- `arch/llvmnet/bits/stdint.h` makes the fast 16- and 32-bit integer types 64-bit, as in glibc. The other x86-64 `bits/` headers are used as they are: LP64, signed `char`, 32-bit `wchar_t`, 80-bit `long double`.
- `crt/llvmnet-start.c` becomes `crt1.o`. Its `__llvmnet_libc_main` initializes musl, runs the program's constructors, calls `main` and `exit`; `__llvmnet_register_finalizer` registers destructors with `atexit`.
- `include/features.h` lets `_GNU_SOURCE` imply `_LARGEFILE64_SOURCE`, as glibc does, so the LFS64 names (`stat64`, `off64_t`) are available to Linux software.
- `include/linux/futex.h` defines the futex constants libc++ uses.
- `src/thread/llvmnet/clone.c` implements `__clone` with `__llvmnet_clone`, which runs the thread on a managed thread and ignores the stack argument.
- `src/thread/llvmnet/pthread_cancel.c` implements cancellation points in C: the host interrupts a cancelled thread's blocking call, and the cancellation point checks the request before and after it.
- `src/exit/llvmnet/exit.c` and `src/thread/llvmnet/__cxa_thread_atexit_impl.c` add a registry of C++ `thread_local` destructors, which run at thread exit and at the start of `exit()`.
- `src/process/llvmnet/posix_spawn.c` starts host processes through `__llvmnet_spawn`, so `posix_spawn`, `system` and `popen` work; `fork` and `exec` are not available.
- `src/signal/llvmnet/restore.c` defines empty `__restore` and `__restore_rt`: the host calls signal handlers as ordinary functions, but `sigaction` needs the symbols.
- `src/string/llvmnet/memcpy.c`, `memmove.c` and `memset.c` call the compiler builtins, which the backend lowers to the runtime's vectorized copy and fill.
- `src/fenv/llvmnet/fenv.c` describes the CIL floating-point environment: round to nearest only and no exception flags. Selecting another rounding mode or raising an exception fails.
- `src/math/llvmnet/roundevenl.c` adds C23's `roundevenl`, which the linker substitutes for `llvm.roundeven.f80`; the other `long double` intrinsics (`llvm.log.f80` and the rest) become calls to musl's generic C functions (`logl`).

Assembly sources under `src/*/x86_64/` are not compiled; musl's generic C implementations, including those of the `long double` math functions, are used.
