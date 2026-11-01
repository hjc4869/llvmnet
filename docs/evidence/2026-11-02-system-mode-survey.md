# System-mode findings from the ANGLE survey (2026-11-02)

The survey of 2026-11-01 and 2026-11-02 built ANGLE in system mode and found three system-mode problems (round two, R4 to R6, of `artifacts/oss/REPORT.md`, which is not in the repository). They are plan items SA-10 (`fork` and `exec`), SA-11 (C++20 libstdc++ members) and SA-12 (`__SSE2__` in system mode), which the survey's own evidence adds to [plan/05-system-abi.md](../plan/05-system-abi.md). This page records what each one was, what was decided, and the commands that show it. The reproducers are `artifacts/oss/results/angle-repro/<name>/run.sh` in the survey's checkout. "Before" is the survey's toolchain at `a5ab005`; "after" is this change, run with copies of the scripts whose `PATH` points at this checkout's `bin/`. The host is Debian 13 with glibc 2.41, .NET 10.0.14 and GCC 16.2's libstdc++.

## SA-10: fork

### What happened

`system-fork-exec/run.sh` runs the pattern of ANGLE's test runner (`util/posix/test_utils_posix.cpp`): `pipe`, `fork`, and in the child `dup2(fds[1], 1)`, `close(fds[0])`, `execv("/bin/echo", ...)`. Before:

```
native: fill=5 <abc> pid=1172184 read=14 'child says hi ' status=0
system run 1: echo: write error: Bad file descriptor (exit 124)
system run 2: echo: write error: Bad file descriptor (exit 124)
system run 3: echo: write error: Bad file descriptor (exit 124)
```

Earlier runs of the same program also ended with the parent killed by `SIGSEGV` (exit 139). `strace -f` shows what the child executed:

```
678984 pipe2([35, 36], 0)               = 0
678984 clone(child_stack=NULL, flags=CLONE_CHILD_CLEARTID|CLONE_CHILD_SETTID|SIGCHLD, ...) = 678991
678984 close(36)                        = 0
678991 dup2(36, 1)                      = 1
678991 dup2(35, 1)                      = 1
678991 execve("/bin/echo", ["/bin/echo", "child says hi"], ...) = 0
```

The child's `close(fds[0])` reached the kernel as `dup2(35, 1)`, so `echo` found stdout closed. Without `fork`, the same `dup2` then `close` sequence is correct. The cause is CoreCLR's W^X protection: generated code and stubs are mapped twice from one shared memory object, an executable view and a writable one. A JIT process's `/proc/<pid>/maps` has 18 `rw-s`, 8 `r-xs` and 13 `---s` mappings of `/memfd:doublemapper (deleted)`. `MAP_SHARED` mappings stay shared across `fork`, and both processes start from the same allocator state, so the child and its parent write the methods and stubs each compiles next into the same pages at the same addresses. One process's code replaces the other's: that is how the child's `close` came to call the `dup2` bridge, and how parents crashed. The same program is correct in every run once the generated code is private:

| Configuration | Runs | Result |
| --- | --- | --- |
| JIT, W^X on (the default) | 3 | wrong descriptor in the child; parent hangs (exit 124) or crashes (exit 139) |
| JIT, `DOTNET_EnableWriteXorExecute=0` | 8 | `read=14 'child says hi' status=0` every time |
| NativeAOT (no generated code) | 5 | `read=14 'child says hi' status=0` every time |
| `vfork` instead of `fork`, JIT with W^X off, and NativeAOT | 3 + 3 | parent crashes: its child returns through and overwrites the frames of the bridge and the P/Invoke stub that the suspended parent resumes in |

### Decision

A forked child goes on running translated code, which is managed code. Besides the shared code pages, only the forking thread exists in the child: a garbage collection or a tiered-compilation suspension in progress at the moment of `fork`, or a runtime lock held by another thread, leaves the child blocked at its next transition, and the child's first call to a method needs the JIT and P/Invoke binding, which take such locks. That cannot be repaired from outside the runtime. The alternatives the task suggested do not apply either:

- Routing a `fork` followed by `exec` through `posix_spawn` or `vfork` semantics needs the child's code to run before the spawn, in the parent's process. ANGLE's child writes memory (`closeEndPoint` sets `fds[1] = -1`, and it builds a `std::vector` of arguments), so emulating the child in place would change the parent's state. Running the child's code natively would mean compiling arbitrary translated functions to native code.
- Refusing `fork` everywhere breaks working programs: SPEC's 500.perlbench_r, 600.perlbench_s and 827.cppcheck_s pass in system mode as NativeAOT executables, and cppcheck's children run the whole analysis.

So the rule follows the cause that was measured: `fork`, `vfork`, `_Fork` and `forkpty` are runtime adapters (`ProcessRuntime.Fork`, `ForkWithoutHandlers`, `ForkPty`, mapped in `HostInterop.Adapter`) that fail with `ENOSYS` while the process has a shared executable mapping, which is what W^X creates, and call the host's function otherwise. `vfork` calls `fork`, which POSIX permits. The check reads `/proc/self/maps` once, since W^X is fixed at startup. `posix_spawn`, `system` and `popen` were never affected: glibc's child runs only C library code before `exec`. A later, opt-in fork under the JIT would need the child to copy the shared code mappings into private memory before it runs managed code; that relies on CoreCLR internals and still races with the parent's background compilation, so it is not done here.

### After

```
native: fill=5 <abc> pid=1025915 read=14 'child says hi ' status=0
system run 1: pid=-1 read=0 '' status=0 (exit 0)
system run 2: pid=-1 read=0 '' status=0 (exit 0)
system run 3: pid=-1 read=0 '' status=0 (exit 0)
```

`tests/system-fork.sh` (core, aot) compiles `tests/c/system-fork.c`, which runs the pattern with `fork`, `vfork` and `_Fork`, a forked worker that reports through its exit status, and `posix_spawn` with file actions. Under the JIT the fork family prints `ENOSYS` and `posix_spawn` matches native; with `DOTNET_EnableWriteXorExecute=0` (three runs) and as NativeAOT the whole output equals native.

## SA-11: C++20 in the system C++ archive

`system-cxx20-sstream/run.sh`, before:

```
c++17: x=42
c++20: FAILS: llvmnet: error: Unresolved function: _ZNKRSt7__cxx1115basic_stringbufIcSt11char_traitsIcESaIcEE3strEv
native c++20: x=42
c++20 + src/c++20/sstream-inst.cc: x=42
```

With `-std=c++20`, `std::stringstream::str()` binds to the C++20 overload `str() const &`, which libstdc++'s headers declare as an extern template and `src/c++20/sstream-inst.cc` defines. `scripts/build-system-cxx.sh` now compiles `src/c++20` as libstdc++.so contains it: the instantiation units (`sstream-inst.cc`, `string-inst.cc`, `cow-string-inst.cc`, `format-inst.cc`) and `format.cc` (as `gnu++26` with `-fno-access-control`, as libstdc++ builds it), `tzdb.cc` (with `tzdata.zi.h` generated from the source tree, since the host's libstdc++ embeds the database, `_GLIBCXX_STATIC_TZDATA`), `atomic.cc`, `clock.cc` and `syncbuf.cc`, plus C++17's `floating_to_chars.cc` and `floating_from_chars.cc`, which `std::format` needs for floating-point values. The archive grows from 90 to 101 units and builds in 44 s. `src/c++23` has no instantiation units: its `print.cc` and `stacktrace.cc` belong to `libstdc++exp`, and a native `std::println` program links without that library. `src/c++17/cow-string-inst.cc` does not compile with Clang ("exception specification in explicit instantiation does not match") and is not needed. After:

```
c++17: x=42
c++20: x=42
native c++20: x=42
c++20 + src/c++20/sstream-inst.cc: x=42
```

`tests/system-cxx.sh` (optional) runs `tests/cpp/cxx20.cpp` (both `str()` overloads, `view()`, `std::format` with integers, strings and floating point, `atomic::wait` and `notify_one`, a semaphore and a latch with `jthread`s, `osyncstream`, and `Europe/Berlin` across the 2026 change to summer time) as C++20 at `-O0` and `-O2` and as C++23 with `std::println`, under the JIT, and as C++20 NativeAOT, all equal to native. The whole test passed in 1 min 56 s.

## SA-12: x86 intrinsics and the SSE macros

`system-x86-intrinsics/run.sh`, before:

```
#define __SSE2__ 1
#define __x86_64__ 1
native: 1 2 2.5 2.5 sad=24 add=42
llvmnet: error: Unsupported intrinsic: llvm.x86.sse2.psad.bw
```

System mode compiles for native x86-64, so Clang predefines `__SSE__`, `__SSE2__`, `__SSE_MATH__`, `__SSE2_MATH__`, `__MMX__` and `__FXSR__`. Libraries choose their SSE paths by them; intrinsics that Clang lowers to generic IR translate, and target intrinsics (`llvm.x86.*`) reached the backend only at the final link, after the whole build. Three changes:

- **Diagnostic while compiling.** After the frontend writes a unit, the driver looks for `llvm.x86.` in the file (bitcode keeps symbol names verbatim, so other units are not parsed again) and warns once per untranslated intrinsic, naming it, the function and, with `-g`, the source location; `-w` silences it. The link error names the intrinsic as an x86 intrinsic and gives the way out. An error at compile time was tried first and rejected: it stopped llvmnet's own system-mode flang-rt build, whose `runtime/exceptions.cpp` reads and writes MXCSR with `_mm_getcsr` and `_mm_setcsr` in functions that only programs using IEEE exception flags reach, and which the link drops otherwise. A unit containing such a function links today and must keep linking.
- **Spin hint and fences translate.** libstdc++'s `<bits/atomic_wait.h>` calls `__builtin_ia32_pause()` on x86-64, so every C++20 atomic wait, semaphore and latch, and `src/c++20/atomic.cc` itself, contain `llvm.x86.sse2.pause`. It becomes `Thread.SpinWait(1)`, and `llvm.x86.sse2.mfence`, `lfence` and `llvm.x86.sse.sfence` become `Thread.MemoryBarrier()`. They carry no vector data.

After (the script prints only the first line of llvmnet's output):

```
#define __SSE2__ 1
#define __x86_64__ 1
native: 1 2 2.5 2.5 sad=24 add=42
llvmnet: warning: x86 target intrinsic llvm.x86.sse2.psad.bw is not translated, so linking a program that reaches it fails; build the code's portable C path instead (system mode predefines __SSE__ and __SSE2__ as native x86-64 does, and libraries usually select their SSE paths by them: try -U__SSE__ -U__SSE2__ or the library's own SIMD option)
```

Whether the diagnostic could reject working programs was checked against llvm-test-suite 22.1.8, whose system-mode run passes 2,013 of 2,049 tests: of its sources that use x86 intrinsics outside `UnitTests/X86`, compiled with `clang-22 -O3 -fno-vectorize -fno-slp-vectorize` and searched for `@llvm.x86.`, only `SingleSource/UnitTests/Vector/SSE/sse.isamax.c` (`llvm.x86.sse.max.ps`) has one, and it already fails, classified as this limitation; `sse.expandfft`, `sse.shift`, `sse.stepfft` and Bullet have none. `tests/x86-intrinsics.sh` (core, aot) checks the warning with and without `-g`, `-w`, the link error, `-U__SSE__ -U__SSE2__` selecting a library-style C path (`tests/c/x86-sse-path.c`, output equal to native), a unit whose unreached `_mm_getcsr` still links, and `tests/c/x86-hints.c` in both modes under the JIT and as system NativeAOT. The support matrix gained an "Intrinsics: x86" section (pause and fences translated, `llvm.x86.sse2.psad.bw` rejected): 188 of 217 constructs are translated.

### Recommendation on the macros

Whether system mode keeps predefining the SSE macros, or translates SSE2 intrinsics instead, is the owner's decision, proposed with the survey. The measurements and the recommendation from this work:

- **(a) Keep the macros.** System mode keeps the native compiler's identity; a library's C path stays a per-project flag (`-U__SSE__ -U__SSE2__`, which `tests/x86-intrinsics.sh` shows working, or the library's own switch), and the warning above names the flag.
- **(b) Stop predefining them, keeping `__x86_64__`,** as portable mode already does (`-U__SSE__ -U__SSE2__ -U__SSE_MATH__ -U__SSE2_MATH__ -U__MMX__ -U__FXSR__` in `PortableAbi.Arguments`). Libraries that test the macros take their C paths without flags. The target still has SSE2, so the floating-point calling convention and `<emmintrin.h>` are unchanged, and code that assumes SSE2 on every x86-64 still meets the diagnostic. It is ABI-visible: declarations keyed on the macros change, so a type shared with a native library built with them can differ in layout. Of the host's headers, glibc's key nothing on them (none of `/usr/include/*.h` or `/usr/include/x86_64-linux-gnu/bits` mentions them); libstdc++'s `simd_fast_mersenne_twister_engine` (`<ext/random>`) and `<simd>`, Eigen's vectorization and alignment, OpenCV's dispatch, OpenCL's `cl_platform.h` and Boost's SIMD detection do. The system C++ archive would be rebuilt with the same macros, and options such as Chromium's `-msse3` would need matching `-U` options or a rejection.
- **(c) Translate the common SSE2 intrinsics** that Clang's headers do not lower to generic IR (the `psad`, `pmadd`, `pack`, `pmulh`, shift-by-vector, `movmsk`, `min` and `max`, `rcp` and `rsqrt` and comparison families), scalarized like other vector code (ADR-10). The SSE paths would then run, but scalarized SIMD is usually slower than a library's C path: the survey's R10 measured Skia's vector kernels at 4 to 23 times a fully scalar native build. A system-mode variant could run each intrinsic natively through a generated bridge, at the cost of a native transition per operation.

Recommended: (b), which makes the common case work without flags and matches portable mode; a program that must share an SSE-keyed layout with a native library defines the macros again with `-D`. (c) stays open as performance work. The compile-time warning and the translated spin hint and fences are needed under every option and are part of this change.

## Gate

```sh
systemd-run --user --scope -q -p MemoryMax=24G -p MemorySwapMax=0 tests/run.sh --tag core --tag toolchain --exclude-tag slow
```

96 passed, 0 failed, 0 timed out, 0 skipped in 709 s (8 jobs), including `system-fork`, `x86-intrinsics`, the regenerated `support-matrix` and `limitations` pages and `jit-validation` over every assembly the run emitted. The sysroots rebuild in 80 s with the compile-time check (no x86 warning outside flang-rt's `exceptions.cpp`), and `tests/system-cxx.sh` (optional) passes with the rebuilt archive.
