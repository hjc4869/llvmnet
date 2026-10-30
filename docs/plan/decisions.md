# Architecture decisions required by the plan

Each decision is recorded in the style of an architecture decision record: context, options, recommendation, consequences, and the milestone that needs it. Status moves from Proposed to Accepted or Rejected after review; keep rejected records for history. Workstream items link here by ID.

| ID | Decision | Needed by | Status |
| --- | --- | --- | --- |
| ADR-01 | Portable C library strategy | M2 → M3 | Accepted 2026-10-30 (spike evidence) |
| ADR-02 | C++ runtime strategy | M2 → M4 | Accepted 2026-10-30 (spike evidence) |
| ADR-03 | Fortran runtime strategy | M2 → M5 | Accepted 2026-10-30 (spike evidence) |
| ADR-04 | Retire `managed-host` mode and the scalar `--native-import` path | M3 | Accepted 2026-10-30 for `managed-host` (implemented on `m3-m5`); `--native-import` stays for system mode until SA-01 |
| ADR-05 | Supported platforms for 1.0 | M6 | Proposed |
| ADR-06 | LLVM version policy | M1 | Proposed |
| ADR-07 | Portable threading model | M2 → M3 | Accepted 2026-10-30 (spike evidence) |
| ADR-08 | Performance target | M8 | Proposed |
| ADR-09 | Portable ABI evolution (`dotnet64-v1` freeze, managed library surface) | M3 (tag), M7 | Tag: working decision 2026-10-30 (`dotnet64-v2`), owner to confirm; freeze and library surface: Proposed |
| ADR-10 | SIMD strategy | post-1.0 | Proposed |
| ADR-11 | Driver defaults | M7 | Proposed |
| ADR-12 | Debug information in 1.0 | M7 | Proposed |
| ADR-13 | License | M0 | Accepted 2026-10-30 (owner) |
| ADR-14 | Linux identity of the portable target | M3 | Working decision 2026-10-30 (option 3), owner to confirm |

## ADR-01: Portable C library strategy

**Context.** The portable runtime is 275 hand-written C library functions in C# plus 30 hand-written headers. Coverage is driven by whichever workload failed last; locale, wide characters, `fenv`, `complex`, most of POSIX and exact printf/scanf/libm semantics are missing, and the implementation has thread-safety and buffering defects (see [LC current state](02-portable-c-library.md#current-state-verified-2026-10-30)). The SPEC portable audit attributes most portable failures to this layer. Reimplementing a complete libc by hand in C# is a multi-year effort with no conformance baseline.

**Options.**

1. Continue the hand-written C# libc, adding functions as workloads require.
2. Compile musl to `dotnet64-v1` bitcode with llvmnet itself, adding an `arch/llvmnet` port whose syscalls go to a small C# host layer (the emscripten and wasi-libc model).
3. Compile picolibc or newlib the same way (smaller, BSD-licensed, no pthreads, thin POSIX layer) and keep threads and POSIX in C#.
4. Compile glibc (rejected immediately: assembly, dynamic linking and Linux-only assumptions throughout).

**Recommendation.** Option 2. musl is complete (locale, wide I/O, `fenv`, `regex`, `getopt`, pthreads, exact printf/strtod, a well-tested libm), MIT-licensed, written in plain C with per-architecture files that are small, has a precedent for syscall-less ports, keeps 80-bit long double on its x86_64 bits which matches the frozen ABI, and has the companion `libc-test` suite. The backend already compiles a full libstdc++ in system mode, so compiling musl is credible. Option 3 is the fallback if the musl thread layer (LC-02) proves infeasible.

**Decision (2026-10-30).** Accepted, after the M2 spike: the owner directed work to proceed through M2, and the spike met every exit condition of LC-01 and LC-02. All 1,345 generic C sources of unmodified musl 1.2.5 compile; stdio, libm (bit-identical double results), file system, time, locale, regex, `setjmp`, signals and musl's own pthreads match native musl under JIT and NativeAOT; nothing resolves to the C# C library. The host interface turned out simpler than planned: Linux x86-64 system-call numbers and semantics implemented over .NET (`__llvmnet_syscall`), plus `__llvmnet_get_tp` and `__llvmnet_clone`. Evidence: `docs/evidence/2026-10-30-m0-m2.md`, `tests/musl.sh`. picolibc was not needed.

**Consequences.** About 40 host functions become the only hand-written OS layer and get a versioned contract. The 275 C# functions, the hand-written headers and the MathNet dependency are deleted. Backend gaps that musl exposes (weak aliases, TLS, atomics, long double formatting) must be fixed first (BE-07, BE-13, BE-16, BE-23). Portable programs gain `setlocale`, wide I/O, `regex.h` and friends without llvmnet-specific code. Performance must be measured (LC-24); musl's `malloc` and stdio are expected to be faster than the current per-call host writes.

## ADR-02: C++ runtime strategy

**Context.** Portable C++ relies on libc++ headers plus a 148-line owned support file and 60 managed ABI exports. There are no iostreams, locale, filesystem or thread lifecycle. System mode already compiles upstream libstdc++ successfully.

**Options.** (1) Keep extending the owned support file and `Cxx.cs`. (2) Compile upstream libc++ and libc++abi to portable bitcode on top of musl, keeping only the exception bridge managed. (3) Compile libstdc++ for portable mode too (GPL with runtime exception, built against glibc headers; awkward on musl).

**Recommendation.** Option 2. It matches the headers already shipped, the license is uniform with LLVM, and the libc++ lit suite provides a conformance baseline. The managed exception bridge stays because unwinding is done by the CLR.

**Decision (2026-10-30).** Accepted with the CX-01 spike: all 66 selected libc++ and libc++abi 22.1.8 sources compile on musl, and a broad smoke program plus 11 fixtures match native under JIT and NativeAOT. For now the managed bridge keeps exceptions, RTTI matching, `dynamic_cast` and terminate handling, and exports libc++abi's `exception_ptr` entry points. Building the runtimes needs `__linux__`/`__unix__` (ADR-14) and `_GNU_SOURCE` for C++ against musl.

**Consequences.** `__cxa_*` ownership must be documented (CX-02). RTTI moves to libc++abi (CX-03). The owned support files and most of `Cxx.cs` are deleted. The libc++ ABI version is pinned with the LLVM release.

## ADR-03: Fortran runtime strategy

**Context.** 126 hand-written `_FortranA*` entries cover what SPEC needed; final procedures, defined I/O, PDTs, most reductions and many editing descriptors are missing. flang-rt is C++17 and sits on a C and C++ standard library.

**Options.** (1) Keep extending the hand-written runtime. (2) Compile flang-rt to portable bitcode on top of musl and libc++, without quad precision until `fp128` exists. (3) Hybrid: flang-rt for everything except I/O.

**Recommendation.** Option 2, which depends on ADR-01 and ADR-02. The upstream runtime is the conformance baseline Flang itself tests against. `REAL(16)` is rejected with a diagnostic until BE-14.

**Decision (2026-10-30).** Accepted with the FT-01 spike: all 67 flang-rt 22.1.8 sources compile on musl and libc++, and 34 Fortran fixtures match native Flang with the C# Fortran runtime hidden. `REAL(16)` stays unavailable because Flang disables `__float128` with libc++.

**Consequences.** The Fortran C# files are deleted. System mode should use the same bitcode built against host headers (FT-09) to avoid two runtimes. FCVS and `llvm-test-suite/Fortran` become gates.

## ADR-04: Retire managed-host mode

**Context.** `managed-host` combines Linux host headers with the managed libc and exists for the original ffmpeg artifacts. `--native-import` predates the system-mode bridge and only supports scalar signatures. Both add code paths and documentation surface.

**Recommendation.** Remove `managed-host` when the hand-written libc is deleted (LC-17), after rebuilding the ffmpeg artifacts in system and portable mode. Fold `--native-import` into `--system-library` semantics or keep it as a documented escape hatch; decide during SA-01.

**Consequences.** Untagged bitcode becomes an error with guidance. Two ABI tags remain: `system-linux-x64-v1` and `dotnet64-v1`.

**Decision (2026-10-30).** Accepted and implemented for `managed-host` on the `m3-m5` branch, as part of the M3 work the owner asked for: `--runtime` defaults to `portable`, `--runtime=managed-host` fails with guidance, untagged link inputs are rejected (`llvmnet --runtime=<mode> -x ir -c` tags LLVM IR), and the tests that used the default now run in both remaining modes. The ffmpeg artifacts are rebuilt in system and portable mode. The remaining tags are `system-linux-x64-v1` and `dotnet64-v2` (ADR-09). `--native-import` stays as a system-mode escape hatch until SA-01.

## ADR-05: Supported platforms for 1.0

**Context.** Portable images are designed for every little-endian 64-bit .NET host; only Linux x86-64 has executed them. Running the compiler itself on Windows and macOS requires more work than running its outputs there.

**Recommendation.** Outputs: Linux x64 and ARM64, Windows x64, macOS ARM64, under JIT and NativeAOT, on .NET 10 (LTS). Compiler host: Linux x64 required for 1.0; Windows and macOS hosts are 1.x goals unless XP-04 lands in time. System ABI: Linux x64 only.

**Consequences.** CI needs ARM64, Windows and macOS runners (FD-19, XP-01..03). Documentation states the matrix explicitly.

## ADR-06: LLVM version policy

**Context.** The compiler binds to `libLLVM-22.so` and `-22` tool suffixes. LLVM releases a new major roughly every six months.

**Recommendation.** Each llvmnet minor release pins exactly one LLVM major; the bundled toolchain variant removes the host dependency. Upgrades are a tracked task per LLVM major with the IR corpus (BE-11/12, WG-01) as the regression net. Supporting two majors at once is not a goal.

**Consequences.** BE-03 isolates the binding. The release notes state the LLVM version.

## ADR-07: Portable threading model

**Context.** Portable pthreads are currently managed objects keyed by the address of the `pthread_mutex_t` storage, looked up per operation. musl expects futex semantics on plain memory and a thread pointer for TLS.

**Recommendation.** Implement `futex` wait/wake in the host layer over managed wait queues keyed by address, spawn threads through a host call that runs musl's thread start on a managed `Thread`, and back the thread pointer with an LLVM TLS global. Validate in LC-02 before committing. Portable OpenMP (FT-08) builds LLVM's `libomp` on the same primitives.

**Decision (2026-10-30).** Accepted with the LC-02 spike. musl's unmodified pthreads run on managed threads through a host `clone` honoring `CLONE_SETTLS`, `CLONE_PARENT_SETTID` and `CLONE_CHILD_CLEARTID`, with futex wait and wake on per-address monitors. Mutexes, condition variables, once, keys, `__thread`, detached threads, semaphores, rwlocks and barriers match native musl in 20 JIT and 10 NativeAOT repetitions.

**Consequences.** pthread objects become plain memory with musl's layout; the managed dictionaries disappear; `pthread_attr_setstacksize` maps to managed thread stack sizes.

## ADR-08: Performance target

**Context.** Portable JIT decoding runs at about 2.5x native elapsed time; the project ordering is correctness first.

**Recommendation.** 1.0 target: portable NativeAOT at or below 2.0x and JIT at or below 2.5x of native geomean on the free benchmark set defined by PF-01, with no correctness regressions. Missing the target does not block 1.0 if the numbers are published and the gap is explained.

## ADR-09: Portable ABI evolution

**Context.** `dotnet64-v1` is documented as fixed. The managed library surface (`--emit-library`, `CExport`) is explicitly not stable for aggregates. The software varargs layout mirrors SysV and is more complex than a managed target needs.

**Measurement (2026-10-30).** The musl sysroot is layout-compatible with the hand-written v1 headers for 23 of 29 checked types (including `stat`, `tm`, `dirent`, `timespec` and the pthread types) but not for `int_fast16_t`, `int_fast32_t` and `uint_fast32_t` (8 versus 4 bytes), `jmp_buf` (144 versus 200), `fpos_t` (8 versus 16) and `BUFSIZ`; v1 also lacked `sigset_t`. The recommendation is therefore to tag the musl sysroot `dotnet64-v2` when it becomes the default (LC-17, LC-26), so v1 objects are rejected rather than mixed, and to freeze v2 at 1.0.

**Working decision (2026-10-30), owner to confirm.** The musl sysroot is tagged `dotnet64-v2` (LC-26) and became the only portable sysroot; `dotnet64-v1` objects are rejected at link time and `__LLVMNET_ABI_VERSION` is 2. v2 is musl's x86-64 layout with one change that keeps glibc's (and v1's) choice: `int_fast16_t`, `int_fast32_t` and their unsigned forms are 64-bit, so programs whose results depend on them behave as native glibc builds do (flang-rt's `REAL(4)` random numbers did not). `jmp_buf`, `fpos_t` and `BUFSIZ` follow musl. The host interface (`docs/dev/host-abi.md`) is versioned with the tag.

**Recommendation.** Freeze `dotnet64-v1` at 1.0 exactly as documented, including the musl-derived header set and the host import table. Any incompatible change (varargs simplification, `wchar_t` width, struct layout) becomes `dotnet64-v2` with explicit mixed-mode rejection, no earlier than 1.x. The managed public surface for 1.0 is scalar and pointer `CExport` methods only, strong-named and versioned (TC-09).

## ADR-10: SIMD strategy

**Context.** SIMD128 exists as an opt-in header path for the ffmpeg HEVC kernels; general vector IR is scalarized.

**Recommendation.** Keep the header path and scalarization for 1.0 (BE-31). General lowering of `<N x T>` to `Vector128`/`Vector256` (BE-32, PF-09) is post-1.0.

## ADR-11: Driver defaults

**Context.** llvmnet defaults to `-O1` with vectorization disabled; Clang defaults to `-O0`. Build systems pass explicit levels in practice.

**Recommendation.** Keep `-O1` scalar as the default because `-O0` IR is much larger for CIL and the output is slower to JIT; document the difference prominently and make sure `-O0` and `-O3` are both fully tested (TC-03).

## ADR-12: Debug information in 1.0

**Context.** No debug information is emitted; stack traces show LLVM symbol names only.

**Recommendation.** Sequence points and a portable PDB (BE-28) are a 1.0 P1 item because production users need `file:line` in crash reports; local variable debugging can follow in 1.x.

## ADR-13: License

**Context.** There is no license file; the project bundles Apache-2.0 (LLVM exception) components and will compile MIT (musl) and Apache-2.0 (libc++, flang-rt) sources into its sysroot.

**Decision (2026-10-30).** Accepted by the owner: Apache-2.0 WITH LLVM-exception, the same license as LLVM (`LICENSE.TXT`, FD-01).

**Recommendation.** Apache-2.0 with LLVM exception for the compiler and runtime, which aligns with the bundled components and the runtime-library exception needed by translated programs; MIT is acceptable if simplicity is preferred. Decide before any external contribution or distribution (FD-01).

## ADR-14: Linux identity of the portable target

**Context.** The portable target triple is freestanding (`x86_64-unknown-none` with `__x86_64__` undefined), so Clang defines neither `__linux__` nor, for C++, `_GNU_SOURCE`. With musl as the C library and the host implementing Linux system-call semantics, the portable environment is in practice a Linux userland on every .NET host. libc++abi only defines `__cxa_thread_atexit` under `__linux__`, libc++ uses Linux paths such as the futex wait, and the spike builds both with `-D__linux__ -D__unix__`. User code is still compiled without them.

**Options.** (1) Define `__linux__` and `__unix__` for all portable code, which makes typical POSIX software take its Linux paths and requires the host to emulate whatever those paths use (`/proc`, `epoll`, `linux/*` headers). (2) Define `__unix__` only, presenting a generic POSIX system. (3) Keep both undefined for user code and define them only when building the runtimes.

**Recommendation.** (3) for 1.0, revisited with the WG-07 ecosystem builds: programs keep a portable identity and do not depend on Linux-only interfaces the host may not provide on Windows or macOS, while the runtimes, which are tested against the host layer, use their Linux code paths.

**Working decision (2026-10-30), owner to confirm.** Option 3, which is what the toolchain does: the sysroot's libc++, libc++abi and flang-rt are built with `-D__linux__ -D__unix__`, while user code sees neither (checked by `tests/c/portable.c`). C++ user code gets `_GNU_SOURCE`, as clang++ defines it on Linux targets.

