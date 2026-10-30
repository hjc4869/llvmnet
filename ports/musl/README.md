# musl port for the dotnet64-v2 ABI

This directory holds the llvmnet port of musl 1.2.5 (plan items LC-01, LC-02). `scripts/build-portable-libc.sh` copies `arch/x86_64` from the pinned upstream source into `arch/llvmnet`, overlays the files here, and compiles every generic C source of musl with llvmnet into LLVM bitcode. No upstream source file is modified.

- `arch/llvmnet/syscall_arch.h` sends every system call to `__llvmnet_syscall`, implemented in C# by `src/LlvmNet.Runtime/HostSyscalls.cs`. The host implements Linux x86-64 system call numbers and semantics over .NET APIs, so it behaves the same on every .NET host.
- `arch/llvmnet/atomic_arch.h` replaces inline assembly with compiler atomics.
- `arch/llvmnet/pthread_arch.h` keeps the thread pointer in a per-thread host slot.
- `arch/llvmnet/crt_arch.h` and `reloc.h` drop the ELF entry point and the dynamic linker.
- `src/process/llvmnet/posix_spawn.c` starts host processes through `__llvmnet_spawn` instead of `clone` and `execve`, so `posix_spawn`, `system` and `popen` work without `fork` (plan item LC-08).
- `src/exit/llvmnet/exit.c` and `src/thread/llvmnet/__cxa_thread_atexit_impl.c` add the C++ `thread_local` destructor registry that glibc has and musl lacks, run at thread exit and at the start of `exit()` (plan item CX-12).
- The x86-64 `bits/` headers are used as they are (LP64, signed `char`, 32-bit `wchar_t`, 80-bit `long double`), except `bits/stdint.h`: the fast 16- and 32-bit integer types are 64-bit as in glibc.

Assembly sources under `src/*/x86_64/` are not used, so musl's generic C implementations of `memcpy`, `fenv`, the `long double` math functions and so on are compiled instead.
