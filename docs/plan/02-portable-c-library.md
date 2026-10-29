# Workstream LC: Portable C library and host layer

Prefix `LC`. Owner: unassigned. Milestones: [M2](README.md#m2-library-architecture-spike) (spike and decision), [M3](README.md#m3-portable-c-library) (delivery).

This is the "library part" that will mostly be replaced. The hand-written C# libc is replaced by an upstream C library compiled to `dotnet64-v1` bitcode by llvmnet itself, on top of a small, versioned host layer written in C#. The approach is the one emscripten and wasi-libc use with musl; the decision is recorded in [ADR-01](decisions.md#adr-01-portable-c-library-strategy).

## Current state (verified 2026-10-30)

- 538 unique `CExport` entry points in `src/LlvmNet.Runtime`: 275 C library functions, 31 pthread functions, 60 C++ ABI symbols, 126 Fortran runtime entries, 46 SIMD128 helpers. The C library portion spans `Stdio`, `Posix`, `CString`, `Formatting`, `Scanning`, `CMath` (with MathNet.Numerics for `erf`/`tgamma`), `CTime`, `CType`, `ProcessRuntime`, `Signals`, `Sorting`, `Parsing`, `CPath`, `Pthreads`.
- 30 hand-written headers (798 lines) under `sysroot/dotnet64-v1/include`, plus Clang's builtin headers. Missing entirely: `locale.h`, `wctype.h`, `fenv.h`, `complex.h`, `sys/mman.h`, `sys/ioctl.h`, `sys/socket.h`, `sys/resource.h`, `sys/utsname.h`, `sys/select.h`, `poll.h`, `regex.h`, `glob.h`, `dlfcn.h`, `pwd.h`, `grp.h`, `termios.h`, `spawn.h`, `iconv.h`, `langinfo.h`, `stdatomic.h`, `threads.h`, `uchar.h`.
- Concrete defects found during this review:
  - `Stdio.descriptors`, `Stdio.files` and `Posix.directories` are plain dictionaries mutated without synchronization, and `nextDescriptor++` is unsynchronized, while other tables (`Posix.mappings`, `Signals.registrations`, `Cxx.typeIds`) are locked; the inconsistency is itself a sign that there is no stated threading model. ffmpeg's worker threads do perform I/O.
  - `printf`, `puts` and `putchar` write straight to the OS handle on every call, bypassing `FILE` buffering, `setvbuf` and any ordering guarantee with `fwrite(stdout)`.
  - `FILE` is a 224-byte opaque block with the descriptor at offset 112, mimicking glibc's layout.
  - errno values and `open` flags appear as magic numbers throughout (`Error(22)`, `flags & 64`).
  - Math goes through `System.Math`, which differs from libm in last-bit results and does not set errno or honor `fenv`.
  - `qsort` is a C# sort calling the comparator through `calli`; `signal` maps only SIGHUP/SIGINT/SIGQUIT/SIGTERM in portable mode.
- The portable SPEC audit (`docs/spec-portable-limitations.md`) already concluded that most of the 81 remaining portable failures are library gaps (locale, streams, POSIX services, math) rather than CIL limits.
- System mode uses the real glibc through generated bridges and is not affected by this workstream, except that it should not accidentally link the portable `libc.a`.

## Target architecture

```
C/C++/Fortran program (portable bitcode)
        |
musl libc as dotnet64-v1 bitcode (unmodified sources + arch/llvmnet port)
        |  ~40 host imports: __llvmnet_host_* (fd I/O, filesystem, mmap, clock, futex, thread spawn, exit, entropy, env)
LlvmNet.Runtime host layer (C#, versioned with the ABI tag)
        |
.NET BCL (FileStream, MemoryMappedFile, Thread, Monitor, RandomNumberGenerator ...)
```

Everything above the host layer is translated C; the host layer is the only hand-written OS abstraction. Its surface is documented in `docs/dev/host-abi.md` and versioned together with `dotnet64-v1`.

## Items

### Spike and decision (M2)

- [ ] **LC-01** (L, P0) musl spike. Add an `arch/llvmnet` port to musl 1.2.x as a patch series under `ports/musl/`: `bits/` copied from `x86_64` (keeps 80-bit long double and the existing type sizes), `syscall_arch.h` routing `__syscallN` to `__llvmnet_host_syscall(n, a1..a6)`, `atomic_arch.h` through `__atomic` builtins, `pthread_arch.h` with `__pthread_self` backed by an LLVM TLS global, and no assembly sources. Compile `src/**/*.c` with `llvmnet --runtime=portable -c` and archive `libc.a`. Exit: `hello world`, `printf("%Lf")`, `qsort`, `setlocale`, `strtod`, `fopen`/`fread`/`fseek` run under JIT and NativeAOT; every backend blocker becomes a BE item.
- [ ] **LC-02** (M, P0) Threads spike on musl: `pthread_create` through `__llvmnet_host_thread_spawn` without `clone`, futex waits through `__llvmnet_host_futex` implemented with managed wait queues keyed by address, TLS through LLVM TLS globals, `pthread_exit`, detached threads, keys and destructors. Validate with `tests/c/threads.c` and the threaded ffmpeg regression. Fallback if infeasible: keep pthreads in C# and exclude musl `src/thread`, with the consequences written down.
- [ ] **LC-03** (S, P0) Record ADR-01 with the spike evidence: go/no-go for musl, and picolibc/newlib as the named fallback.

### Host layer (M3)

- [ ] **LC-04** (M, P0) Define the host import surface in `docs/dev/host-abi.md`: numbered entry points with fixed scalar signatures, Linux-compatible constants baked into musl's `bits/`, a central errno mapping from .NET exceptions, and the rule that every host call returns `-errno` like a Linux syscall. No magic numbers in C#.
- [ ] **LC-05** (M, P0) File descriptors: `open`, `close`, `read`, `write`, `pread`, `pwrite`, `lseek`, `fstat`, `ftruncate`, `fsync`, `dup`, `dup2`, `fcntl` (`F_GETFL`/`F_SETFL`/`F_DUPFD`), `pipe` (managed pipes), `isatty`, `ioctl(TIOCGWINSZ)`; descriptor table with locking and a leak check at exit. Standard streams map to the console with UTF-8.
- [ ] **LC-06** (M, P0) Filesystem: `stat`/`lstat`, `getcwd`, `chdir`, `mkdir`, `rmdir`, `unlink`, `rename`, `access`, `chmod`, `utimensat`, `readlink`, `symlink`, `realpath` support, directory reading as a `getdents`-style host call, `statfs` minimal.
- [ ] **LC-07** (M, P0) Memory: anonymous `mmap`/`munmap` over `NativeMemory`, file-backed `mmap` (read-only and private writable) over `MemoryMappedFile`, `mprotect`/`madvise` as documented no-ops, `brk` as an arena. Decide whether musl's `mallocng` runs over this (recommended: deterministic behavior and `malloc_usable_size` parity) or `malloc` is routed to `NativeMemory`; measure both on ffmpeg before deciding.
- [ ] **LC-08** (M, P0) Process: `exit`/`_exit`/`abort` (abort reports the translated stack and terminates), `getpid`/`getppid`, `raise`/`kill(self)`, `sigaction` for SIGINT/SIGTERM/SIGHUP through `PosixSignalRegistration`, synchronous faults documented as undeliverable, `fork`/`exec`/`posix_spawn` returning ENOSYS, and `system()`/`popen()` through `Process.Start` (POV-Ray in SPEC needs `system`). Behavior is identical on every host OS.
- [ ] **LC-09** (S, P0) Time: `clock_gettime` for REALTIME, MONOTONIC, PROCESS_CPUTIME_ID and THREAD_CPUTIME_ID, `nanosleep`, timezone data for musl (`TZ` and `/etc/localtime`, with a host fallback from `TimeZoneInfo` on Windows and macOS).
- [ ] **LC-10** (S, P0) Startup: build `argv`/`environ`, call musl's start path (`__libc_start_main` equivalent `__llvmnet_start`), run constructors in musl order, propagate the exit code; `uname` filled from `RuntimeInformation`.
- [ ] **LC-11** (S, P1) Entropy: `getrandom`/`getentropy` through `RandomNumberGenerator`; `/dev/urandom` emulated as a path.
- [ ] **LC-12** (M, P1) Identity: `getuid`/`geteuid`/`getgid`, minimal `getpwuid`/`getpwnam`, `gethostname`.
- [ ] **LC-13** (M, P2) Sockets: ENOSYS in 1.0, documented; later over .NET sockets.
- [ ] **LC-14** (S, P0) Error semantics tests: each host call has a test for its documented errno results (missing file, permission, bad descriptor, directory versus file) in JIT and NativeAOT.

### Library build and packaging (M3)

- [ ] **LC-15** (M, P0) `scripts/build-portable-libc.sh`: fetch the pinned musl tarball by hash, apply `ports/musl/*.patch`, compile with llvmnet at `-O2` without assembly, produce `sysroot/dotnet64-v1/lib/libc.a` plus the start object, install musl's `include/` and generated `bits/`, and delete the 30 hand-written headers. The script is idempotent and runs in CI.
- [ ] **LC-16** (S, P0) Driver defaults: portable links `libc.a` and the start object automatically; `-nostdlib`/`-nostartfiles` opt out; `-lm`, `-lpthread`, `-lrt`, `-ldl` are accepted no-ops; include paths come from the sysroot only.
- [ ] **LC-17** (M, P0) Delete the hand-written C library once parity holds: `Stdio`, `Posix`, `CString`, `Formatting`, `Scanning`, `CMath`, `CTime`, `CType`, `Sorting`, `Parsing`, `CPath`, `Pthreads`, `Signals` and most of `ProcessRuntime`. Keep `Float80`, `WideInteger`, `Simd128`, `Atomics` (until BE-16), the host layer, the C++ exception bridge and the Fortran glue pending [FT](04-fortran-runtime.md). Drop the MathNet.Numerics dependency. Remove the `managed-host` mode in the same change ([ADR-04](decisions.md#adr-04-retire-managed-host-mode)).
- [ ] **LC-18** (M, P0) Audit the remaining managed-to-C `calli` sites (thread start, signal dispatch, exit callbacks) and their signatures against [BE-18](01-compiler-backend.md); every remaining site is listed in `host-abi.md`.

### Conformance and tests (M3)

- [ ] **LC-19** (M, P0) Build and run the musl project's companion `libc-test` suite in portable mode under JIT and NativeAOT; record pass/fail per test; classify failures as host-layer gap, backend bug or expected (ENOSYS). Target: all functional tests pass except the documented ENOSYS set.
- [ ] **LC-20** (M, P0) printf/scanf/strtod exactness against native glibc and musl for a corpus including `%a`, `%Lf`, `%n`, `%'d`, positional arguments, wide strings and `C.UTF-8`; only `C` and `C.UTF-8` locales are supported in 1.0, documented.
- [ ] **LC-21** (M, P0) libm accuracy: musl's libm compiled to CIL compared with the host libm on random inputs per function, reporting maximum ULP error, so `System.Math` divergences disappear.
- [ ] **LC-22** (M, P0) Threads: the threaded ffmpeg regression, dav1d workers, `pthread_cond_timedwait`, `pthread_once`, TLS destructors, detached threads at exit, and a 10,000-thread create/join stress, in JIT and NativeAOT.
- [ ] **LC-23** (S, P0) Re-run the 81-benchmark portable SPEC audit and reclassify each remaining failure; update `docs/spec-portable-limitations.md` with the post-musl state.
- [ ] **LC-24** (M, P1) Performance guard: `fwrite` of 1 GB, `malloc`/`free` microbenchmark, printf-heavy program, and the ffmpeg decode benchmark must not regress versus the hand-written runtime; results in [PF-01](08-performance.md).
- [ ] **LC-25** (S, P1) System mode isolation: `--runtime=system` never links the portable `libc.a` or sysroot headers; `tests/modes.sh` checks it.

## Exit criteria for M3

LC-04 through LC-10, LC-14 through LC-17, LC-19 through LC-23 are done; the portable ffmpeg profile, all 31 media fixtures and the private 4K HDR full-file hash gate pass; portable SPEC passes are at parity with system mode except for entries whose cause is a documented ABI restriction.
