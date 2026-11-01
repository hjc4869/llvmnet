# Architecture decisions required by the plan

Each decision is recorded in the style of an architecture decision record: context, options, recommendation, consequences, and the milestone that needs it. Status moves from Proposed to Accepted or Rejected after review; keep rejected records for history. Workstream items link here by ID.

| ID | Decision | Needed by | Status |
| --- | --- | --- | --- |
| ADR-01 | Portable C library strategy | M2 → M3 | Accepted 2026-10-30 (spike evidence) |
| ADR-02 | C++ runtime strategy | M2 → M4 | Accepted 2026-10-30 (spike evidence) |
| ADR-03 | Fortran runtime strategy | M2 → M5 | Accepted 2026-10-30 (spike evidence) |
| ADR-04 | Retire `managed-host` mode and the scalar `--native-import` path | M3 | Accepted 2026-10-30 for `managed-host` (implemented on `m3-m5`); accepted 2026-10-31 for `--native-import` (removal, SA-01) |
| ADR-05 | Supported platforms for 1.0 | M6 | Accepted 2026-10-31 (owner) |
| ADR-06 | LLVM version policy | M1 | Accepted 2026-10-31 (owner) |
| ADR-07 | Portable threading model | M2 → M3 | Accepted 2026-10-30 (spike evidence) |
| ADR-08 | Performance target | M8 | Deferred 2026-10-31 (owner): no numeric target for 1.0 |
| ADR-09 | Portable ABI evolution (`dotnet64-v1` freeze, managed library surface) | M3 (tag), M7 | Accepted 2026-10-31 (owner): `dotnet64-v2`, frozen at 1.0 with `-funsigned-char` allowed; scalar and pointer `CExport` surface |
| ADR-10 | SIMD strategy | post-1.0 | Accepted 2026-10-31 (owner) |
| ADR-11 | Driver defaults | M7 | Accepted 2026-10-31 (owner) |
| ADR-12 | Debug information in 1.0 | M7 | Accepted 2026-10-31 (owner) |
| ADR-13 | License | M0 | Accepted 2026-10-30 (owner) |
| ADR-14 | Linux identity of the portable target | M3 | Accepted 2026-10-31 (owner, option 3); parity exclusions extended 2026-10-31 |

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

**Decision on `--native-import` (2026-10-31).** Accepted by the owner: the option is removed. `--system-library` generates bridges for every scalar signature `--native-import` handles, and for aggregates and callbacks as well, so one path remains to test and document. The option fails with guidance pointing at `--system-library` (SA-01).

## ADR-05: Supported platforms for 1.0

**Context.** Portable images are designed for every little-endian 64-bit .NET host; only Linux x86-64 has executed them. Running the compiler itself on Windows and macOS requires more work than running its outputs there.

**Recommendation.** Outputs: Linux x64 and ARM64, Windows x64, macOS ARM64, under JIT and NativeAOT, on .NET 10 (LTS). Compiler host: Linux x64 required for 1.0; Windows and macOS hosts are 1.x goals unless XP-04 lands in time. System ABI: Linux x64 only.

**Decision (2026-10-31).** Accepted by the owner as recommended. Compiler hosts on Windows and macOS (XP-04) are 1.x goals; system-ABI modes on those hosts are out of scope (XP-08).

**Consequences.** CI needs ARM64, Windows and macOS runners (FD-19, XP-01..03). Documentation states the matrix explicitly.

## ADR-06: LLVM version policy

**Context.** The compiler binds to `libLLVM-22.so` and `-22` tool suffixes. LLVM releases a new major roughly every six months.

**Recommendation.** Each llvmnet minor release pins exactly one LLVM major; the bundled toolchain variant removes the host dependency. Upgrades are a tracked task per LLVM major with the IR corpus (BE-11/12, WG-01) as the regression net. Supporting two majors at once is not a goal.

**Decision (2026-10-31).** Accepted by the owner as recommended. llvmnet stays on LLVM 22 until an upgrade is planned as its own task.

**Consequences.** BE-03 isolates the binding. The release notes state the LLVM version.

## ADR-07: Portable threading model

**Context.** Portable pthreads are currently managed objects keyed by the address of the `pthread_mutex_t` storage, looked up per operation. musl expects futex semantics on plain memory and a thread pointer for TLS.

**Recommendation.** Implement `futex` wait/wake in the host layer over managed wait queues keyed by address, spawn threads through a host call that runs musl's thread start on a managed `Thread`, and back the thread pointer with an LLVM TLS global. Validate in LC-02 before committing. Portable OpenMP (FT-08) builds LLVM's `libomp` on the same primitives.

**Decision (2026-10-30).** Accepted with the LC-02 spike. musl's unmodified pthreads run on managed threads through a host `clone` honoring `CLONE_SETTLS`, `CLONE_PARENT_SETTID` and `CLONE_CHILD_CLEARTID`, with futex wait and wake on per-address monitors. Mutexes, condition variables, once, keys, `__thread`, detached threads, semaphores, rwlocks and barriers match native musl in 20 JIT and 10 NativeAOT repetitions.

**Consequences.** pthread objects become plain memory with musl's layout; the managed dictionaries disappear; `pthread_attr_setstacksize` maps to managed thread stack sizes.

## ADR-08: Performance target

**Context.** Portable JIT decoding runs at about 2.5x native elapsed time; the project ordering is correctness first.

**Recommendation.** 1.0 target: portable NativeAOT at or below 2.0x and JIT at or below 2.5x of native geomean on the free benchmark set defined by PF-01, with no correctness regressions. Missing the target does not block 1.0 if the numbers are published and the gap is explained.

**Decision (2026-10-31).** Deferred by the owner: 1.0 has no numeric performance target, because correctness comes before optimization. The M8 items stay in the plan and are done without a numeric bar; PF-01 still publishes numbers against native and fails the nightly on regressions. R7 and RL-05 require published numbers, not a target. The target is revisited once the 1.0 correctness gates pass.

## ADR-09: Portable ABI evolution

**Context.** `dotnet64-v1` is documented as fixed. The managed library surface (`--emit-library`, `CExport`) is explicitly not stable for aggregates. The software varargs layout mirrors SysV and is more complex than a managed target needs.

**Measurement (2026-10-30).** The musl sysroot is layout-compatible with the hand-written v1 headers for 23 of 29 checked types (including `stat`, `tm`, `dirent`, `timespec` and the pthread types) but not for `int_fast16_t`, `int_fast32_t` and `uint_fast32_t` (8 versus 4 bytes), `jmp_buf` (144 versus 200), `fpos_t` (8 versus 16) and `BUFSIZ`; v1 also lacked `sigset_t`. The recommendation is therefore to tag the musl sysroot `dotnet64-v2` when it becomes the default (LC-17, LC-26), so v1 objects are rejected rather than mixed, and to freeze v2 at 1.0.

**Decision (2026-10-30, confirmed by the owner 2026-10-31).** The musl sysroot is tagged `dotnet64-v2` (LC-26) and became the only portable sysroot; `dotnet64-v1` objects are rejected at link time and `__LLVMNET_ABI_VERSION` is 2. v2 is musl's x86-64 layout with one change that keeps glibc's (and v1's) choice: `int_fast16_t`, `int_fast32_t` and their unsigned forms are 64-bit, so programs whose results depend on them behave as native glibc builds do (flang-rt's `REAL(4)` random numbers did not). `jmp_buf`, `fpos_t` and `BUFSIZ` follow musl. The host interface (`docs/dev/host-abi.md`) is versioned with the tag.

**Recommendation (before v2).** Freeze `dotnet64-v1` at 1.0 exactly as documented, including the musl-derived header set and the host import table. Any incompatible change (varargs simplification, `wchar_t` width, struct layout) becomes `dotnet64-v2` with explicit mixed-mode rejection, no earlier than 1.x. The managed public surface for 1.0 is scalar and pointer `CExport` methods only, strong-named and versioned (TC-09). Superseded by the v2 tag above and the freeze below.

**Freeze (2026-10-31).** Accepted by the owner: `dotnet64-v2` is frozen at 1.0 as documented (header set, host import table, varargs layout, `long double`, struct layouts; RL-02). One change goes in before the freeze: `-funsigned-char` and `-fno-signed-char` are allowed in portable mode (BE-41). Char signedness changes no layout, only how `char` values are extended and the `CHAR_MIN`/`CHAR_MAX` macros, so objects compiled either way link together without a check, as they do natively; the sysroot stays built with signed `char`. The software varargs layout keeps mirroring SysV: the slot-array simplification (BE-19) and any other incompatible change wait for `dotnet64-v3` after 1.0.

**Managed library surface (2026-10-31).** Accepted by the owner: the 1.0 public surface of `--emit-library` is scalar and pointer `CExport` methods only, strong-named and versioned, with a generated C header and a sample project (TC-09). Aggregate signatures stay unstable.

## ADR-10: SIMD strategy

**Context.** SIMD128 exists as an opt-in header path for the ffmpeg HEVC kernels; general vector IR is scalarized.

**Recommendation.** Keep the header path and scalarization for 1.0 (BE-31). General lowering of `<N x T>` to `Vector128`/`Vector256` (BE-32, PF-09) is post-1.0.

**Decision (2026-10-31).** Accepted by the owner as recommended.

## ADR-11: Driver defaults

**Context.** llvmnet defaults to `-O1` with vectorization disabled; Clang defaults to `-O0`. Build systems pass explicit levels in practice.

**Recommendation.** Keep `-O1` scalar as the default because `-O0` IR is much larger for CIL and the output is slower to JIT; document the difference prominently and make sure `-O0` and `-O3` are both fully tested (TC-03).

**Decision (2026-10-31).** Accepted by the owner: `-O1` without vectorization stays the default, and the driver reference states the difference from Clang's `-O0` (TC-03).

## ADR-12: Debug information in 1.0

**Context.** No debug information is emitted; stack traces show LLVM symbol names only.

**Recommendation.** Sequence points and a portable PDB (BE-28) are a 1.0 P1 item because production users need `file:line` in crash reports; local variable debugging can follow in 1.x.

**Decision (2026-10-31).** Accepted by the owner: BE-28 is P1, so it ships in 1.0 if ready and does not block the release. Local variables follow in 1.x.

## ADR-13: License

**Context.** There is no license file; the project bundles Apache-2.0 (LLVM exception) components and will compile MIT (musl) and Apache-2.0 (libc++, flang-rt) sources into its sysroot.

**Recommendation.** MIT for the compiler and runtime: simple, permissive, and compatible with the bundled Apache-2.0 (LLVM exception) and MIT components. Decide before any external contribution or distribution (FD-01).

**Decision (2026-10-30).** Accepted by the owner: MIT (`LICENSE.TXT`, FD-01). This supersedes the earlier choice of Apache-2.0 WITH LLVM-exception, made the same day.

## ADR-14: Linux identity of the portable target

**Context.** The portable target triple is freestanding (`x86_64-unknown-none` with `__x86_64__` undefined), so Clang defines neither `__linux__` nor, for C++, `_GNU_SOURCE`. With musl as the C library and the host implementing Linux system-call semantics, the portable environment is in practice a Linux userland on every .NET host. libc++abi only defines `__cxa_thread_atexit` under `__linux__`, libc++ uses Linux paths such as the futex wait, and the spike builds both with `-D__linux__ -D__unix__`. User code is still compiled without them.

**Options.** (1) Define `__linux__` and `__unix__` for all portable code, which makes typical POSIX software take its Linux paths and requires the host to emulate whatever those paths use (`/proc`, `epoll`, `linux/*` headers). (2) Define `__unix__` only, presenting a generic POSIX system. (3) Keep both undefined for user code and define them only when building the runtimes.

**Recommendation.** (3) for 1.0, revisited with the WG-07 ecosystem builds: programs keep a portable identity and do not depend on Linux-only interfaces the host may not provide on Windows or macOS, while the runtimes, which are tested against the host layer, use their Linux code paths.

**Decision (2026-10-30, confirmed by the owner 2026-10-31).** Option 3 for user code, which sees neither macro (checked by `tests/c/portable.c`); C++ user code gets `_GNU_SOURCE`, as clang++ defines it on Linux targets. The libc++ conformance run (CX-08) showed that the runtimes must see the same macros as user code: libc++'s headers choose ABI details such as the atomic-wait contention type by `__linux__`, so a library built with it did not link with programs built without it. libc++, libc++abi and flang-rt are therefore built without `__linux__` and `__unix__`; only libc++abi's `cxa_thread_atexit.cpp`, which defines `__cxa_thread_atexit` solely under `__linux__` and exposes no header ABI, still gets it. libc++ then uses its portable atomic-wait table instead of futexes, and plain `read`/`write` for `copy_file`.

**Evidence from the SPEC audit (LC-23, 2026-10-30).** With option 3, eight portable SPEC failures (four benchmarks, each in rate and speed) come only from platform detection that keys on `__linux__`: xalancbmk selects Xerces' Linux platform file only when it sees the macro, and otherwise has no platform; LLVM includes `<machine/endian.h>` instead of `<endian.h>`; gem5's libelf defines `roundup2` only for glibc or Linux; marian's path library stops with "Unsupported system". zstd detects POSIX through `__unix__` and passes with its documented `PLATFORM_POSIX_VERSION` override. Each would need the macro or a per-benchmark workaround; options 1 and 2 trade that for the Linux-only interfaces such code then expects. The owner's decision should weigh these against the portability argument above.

**Further exclusions (accepted by the owner 2026-10-31).** After the M3 to M5 close-out, two further classes of entries pass natively against glibc and libstdc++ but not in portable mode, and no documented flag changes that: programs whose source relies on glibc or libstdc++ behavior that musl and libc++ do not share, and that fail natively against musl too (507.cactuBSSN_r and 607.cactuBSSN_s: overlapping `sprintf`; 737.gmsh_r: `bool` from `NULL`, in a line SPEC added; 748.flightdm_r: the GNU `strerror_r` under `_GNU_SOURCE`, which libc++'s headers need against musl); and prototypes that contradict the definition, which llvmnet rejects where native code reads a register that happens to line up (722.palm_r and 822.palm_s: four-argument `open`; 621.wrf_s: implicitly `REAL` `omp_get_thread_num`). Both are documented in [dev/spec.md](../dev/spec.md#portable-mode). Both classes are parity exclusions alongside `fork`, since each needs a source change the SPEC rules do not allow. With them, every CPU 2017 and CPU 2026 entry that passes in system mode passes in portable mode or is a listed exclusion.

**Owner's decision on this evidence (2026-10-31).** Option 3 stays. A program whose platform detection needs `__linux__` builds with a documented per-program flag (for example `-D__linux__` for xalancbmk, LLVM, gem5 and marian in `config/spec/llvmnet.cfg`) rather than a toolchain-wide identity; the flag makes that program, not llvmnet, responsible for the Linux-only interfaces it then uses (LC-31). This also defines SPEC portable parity for the M3, M4 and M5 exits: every entry passes in portable mode where it passes in system mode, with documented per-benchmark portability flags allowed, except listed exclusions. The exclusions are programs that need `fork` or `vfork`, which stay `ENOSYS` permanently because .NET cannot duplicate a process (perlbench rate and speed, cppcheck), and portable ABI restrictions (PALM's four-argument `open`).

## Proposed for the owner (2026-10-31)

Questions that came up while closing M7 and that are the owner's to decide; nothing below has been acted on.

- **Publishing the dotnet tool package to nuget.org.** The release workflow builds `llvmnet.<version>.nupkg` (19 MB: the compiler, the LLVM bridge and the sysroots; LLVM 22 on `PATH`) and attaches it to the GitHub release. Pushing it to nuget.org needs an API key held by the owner and reserves the `llvmnet` package id; the proposal is to attach only, until a 0.x release is worth announcing.
- **Signing identity.** `.github/workflows/release.yml` signs every artifact with cosign's keyless signing through the workflow's GitHub OIDC token (Sigstore bundle next to each file, verified against the repository's workflow identity), which needs no key to keep or rotate. The alternative in TC-11, minisign with a key the owner holds, gives a signature that does not depend on GitHub and Sigstore being reachable at verification time. The proposal is keyless signing; a minisign key can be added later without changing the artifacts.
- **Release naming.** The archives are `llvmnet-<version>-linux-x64.tar.xz` (thin, self-contained compiler, LLVM 22 expected on `PATH`) and `llvmnet-<version>-linux-x64-llvm.tar.xz` (LLVM bundled); the proposal is to recommend the bundled one to users and the thin one to distributions that package LLVM themselves.

## Proposed for the owner (2026-11-01, M8)

Questions from PF-01 that are the owner's to decide. The harness implements the proposals below as its defaults, so the nightly job runs today, and each can be changed in one place (`tests/perf/benchmarks.tsv`, the constants at the top of `scripts/benchmark.py`, `tests/perf/baseline.tsv`).

- **Benchmark set.** Proposed: the 11 microbenchmarks in `tests/perf/` (malloc, fwrite, printf, atomics, mutex, calli, varargs, Float80, memcpy, alloca, callback) and a startup probe reported but left out of the means, 42 free programs from `llvm-test-suite` 22.1.8 (SingleSource Benchmarks, Olden, Ptrdist, MiBench, tramp3d-v4, Bullet, mafft, nbench) and the ffmpeg decode harness on two clips the nightly job generates from ffmpeg's own test pattern (1080p HEVC 10-bit and H.264, 240 frames, one and four threads). Both compilers get the same pipeline, `-O2 -fno-vectorize -fno-slp-vectorize` (llvmnet scalarizes vectors, ADR-10, so the ratio measures the translation, not the vectorizer); native is glibc. Outputs must equal native (or, for a program that depends on glibc's `rand`, a native musl build). Left out: PAQ8p (`fork`), sim (a prototype that contradicts its definition), MiBench gsm (does not build against glibc's headers). The plan item also names zstd and x264: neither is pinned in `scripts/sources.lock` today; the proposal is to add upstream zstd (BSD) and x264 (GPLv2, built and run only, never redistributed) as pinned sources in a later change if the owner wants them, since `llvm-test-suite`'s compression and codec programs (MiBench jpeg and lame, ffmpeg itself) cover the same ground.
- **Regression policy.** Proposed: a benchmark fails the nightly when its llvmnet time (the fastest of its runs, since noise only adds time and short JIT runs can be bimodal), divided by the host's speed relative to the baseline host (the geometric mean of native times over baseline native times), exceeds its baseline by more than 25%; an engine (JIT, NativeAOT) fails when the geometric mean of those quotients exceeds 1.10. A regression must reproduce in a second measurement. Faster results never fail; they are marked so the baseline can be refreshed.
- **Baseline refresh.** Proposed: the committed baseline comes from the build host (AMD Ryzen AI MAX+ 395, 32 threads); the first nightly on GitHub's runner writes a runner baseline with `--update-baseline`, which is committed as the reference from then on. Afterwards the baseline changes only in a commit that says which change moved which numbers (a performance item, a runner image change), never to silence a regression.
- **NativeAOT instruction set for SIMD128 builds (PF-08).** The owner decided that baseline stays the default and `x86-64-v3` is an opt-in; both are implemented. Measured since: the default is ILC's `x86-64` level, and what the SIMD128 profile lacks there is SSSE3 (`pshufb`), which `x86-64-v2` (SSE4.2, POPCNT; every x64 CPU since about 2009 to 2011) already provides; the probe's shuffles run as fast at `x86-64-v2` as at `x86-64-v3`. Proposed: either make `x86-64-v2` the default for `--simd128` builds, or offer it as a third value, so SIMD128 executables avoid the software shuffles without requiring AVX2. Nothing is changed until the owner decides.

## Proposed for the owner (2026-11-01, M9)

Questions from the M9 workload gates and the 1.0 checklist that are the owner's to decide. The gates implement the proposals below as their defaults, so the nightly jobs run today; each threshold is one variable in `.github/workflows/ci.yml` ([dev/gates.md](../dev/gates.md)).

- **Conformance thresholds (WG-09).** Proposed: each suite's threshold is its current lowest group rate rounded down to a whole percent, so one new failure in the smallest group fails the nightly; thresholds are raised when fixes land and never lowered to absorb a regression. A failure that is accepted rather than fixed is classified (llvm-test-suite) or documented with its cause (the other suites) in the same change.
- **What WG-01's 99% counts.** Proposed: the llvm-test-suite rate that the threshold applies to leaves out the failures `tests/conformance/llvm-test-suite.tsv` classifies as x86 instruction-set tests in portable mode, which has no x86 instruction set by design, and as the three exclusion classes already accepted for SPEC (`fork`, glibc or libstdc++ behavior that musl and libc++ do not share, prototypes that contradict the definition); llvmnet's own limitations (computed goto, inline assembly with instructions, `__builtin_setjmp`, `__builtin_return_address`, rounding modes) and open plan items count against it. The raw rate is published next to it. Three programs that choose their platform code by `__linux__` build with it as a per-program setting, as the SPEC benchmarks do.
- **gcc-c-torture source (WG-02).** The plan names GCC's `c-torture` execute tests. Proposed: use the copy in the pinned `llvm-test-suite` 22.1.8 (`SingleSource/Regression/C/gcc-c-torture`, 1,488 tests that build natively with Clang), with that suite's own list of GCC extensions Clang does not implement, rather than pinning a GCC source tree for its test directory alone.
- **Configure probes that call a function without its arguments (TC-07, WG-04, WG-07).** ffmpeg's `check_func` links `extern int f(); int main(void){ f(); }`, and autoconf's `AC_CHECK_FUNCS` links a call with no arguments through a `char f()` declaration. llvmnet rejects such a call in portable mode, as a prototype that contradicts the definition (the 2026-10-31 decision), so these scripts silently leave out every probed function that takes parameters: 18 in ffmpeg (`mkstemp`, `mmap`, `isatty`, `sysconf` and others), which cost two FATE tests and 104 skipped ones until the harness patched the probe. System mode is not affected. Options: (a) keep the rejection and document that portable builds need patched probes or cache variables; (b) compile a direct call that passes fewer arguments than the definition into a runtime trap with the same diagnostic instead of a compile error, so a link-only probe succeeds and a program that executes the call still stops (`--trap-missing-arguments` does this today for void calls, when asked). Proposed: (b) for fewer arguments only, the probe's shape; every other contradiction stays a compile error, and SPEC's exclusions are unchanged.
- **Reporting vulnerabilities (RL-08).** `SECURITY.md` proposes GitHub's private vulnerability reporting, which needs the repository on GitHub (FD-19) with the feature enabled. The alternative is a contact address the owner chooses.
- **Support and deprecation terms (RL-08).** `docs/user/support.md` marks as proposed: fixes for the latest 1.x minor release; security fixes for the previous minor release for three months after the next one ships; security fixes for the last 1.x minor release for twelve months after 2.0 or until .NET 10's end of support, whichever comes first; a 1.x minor release may add a newer .NET LTS but not drop .NET 10; objects from an earlier 1.x release stay valid link inputs; deprecation for at least one minor release and three months with a driver warning, removal only in a major release.

## Proposed for the owner (2026-11-02, library survey)

Questions from the [library survey](../evidence/2026-11-02-library-survey.md), which built 18 open-source libraries (round one from LightPlayer-Packages, round two AvaloniaStatic's Skia, ANGLE and their dependencies) and ran their own suites against native builds. The priorities of the new items are proposals too.

- **New items and their priorities.** BE-44 (`musttail`) is proposed as P0, because the translation silently drops a guarantee the IR states and the program crashes. TC-19 to TC-22, BE-45, BE-46, PF-15, PF-16 and SA-10 to SA-12 are proposed as P1, TC-23 and LC-32 as P2.
- **Configure probes, continued (TC-07).** The M9 proposal (b) above traps a call that passes fewer arguments than the definition. Autoconf's and Meson's probes also declare another return type (`char f(void)` against `void jpeg_mem_src(...)` or `struct tm *gmtime_r(...)`), so (b) as written leaves them failing: the survey found LibRaw built without JPEG, lcms2 without its mutexes and HarfBuzz without FreeType's variable fonts, with no error. Proposed: extend (b) to any call through a prototype without parameters, whatever its return type. CMake errs the other way: its probes are compiled but never linked, so `check_function_exists` and `check_c_source_compiles` succeed for anything (brotli concluded the compiler was Emscripten). Proposed: probes link and run executables, as `try_run` already does through `env`, at about 1.5 s per probe.
- **`SHARED` targets and `UNIX` in the CMake toolchain file (TC-07).** `CMAKE_SYSTEM_NAME Generic` keeps host libraries out of the search paths, but also leaves `UNIX` unset and makes every `SHARED` target a configure error. Options for `SHARED`: (a) keep the error and document each project's static-only switch; (b) build such targets as static bitcode archives, which is what zlib's default configuration, libpng's tests and libjpeg-turbo's dual build need; (c) build them as `-shared` CIL libraries, which cannot contain the C library ([linking.md](../user/linking.md)), so most C libraries would fail when run. Proposed: (b), and `UNIX` set without CMake's Unix search paths.
- **Host macros (ADR-14).** The survey's projects needed a setting wherever they choose platform code by host macros: Skia `-DSK_BUILD_FOR_UNIX` (without it Skia builds for macOS), ANGLE, libdrm and googletest `-D__linux__`, brotli `-DBROTLI_BUILD_64_BIT` (without it brotli takes its 32-bit paths, 16% slower), FreeType and libpng `-DUNIX=ON`. Defining `__unix__` alone would have fixed Skia, not the others. Proposed: keep ADR-14 as accepted, remove the x86 macros that are still predefined (TC-22), and list these settings in [troubleshooting.md](../user/troubleshooting.md).
- **x86 feature macros in system mode (SA-12).** System mode is the native x86-64 ABI, for which Clang predefines `__SSE2__` and related macros, but translated code cannot execute x86 intrinsics, so libraries that test the macros fail at the final link. Options: (a) translate the common SSE2 intrinsics to `Vector128` (large, comparable to PF-09); (b) undefine the SIMD feature macros in system mode as portable mode does, so libraries take their generic paths (the data layout does not change; code that requires SSE then fails to compile, as in portable mode); (c) keep the macros and diagnose an intrinsic at compile time. Proposed: (b), with the diagnostic of (c) for code that includes the intrinsic headers regardless.
- **What WG-07 runs.** The plan lists zlib, bzip2, xz, sqlite, lua, zstd, libpng, libjpeg-turbo, expat and pcre2. The survey's suites that compare with native bit for bit, and that found the defects above, are libjpeg-turbo's and libpng's ctest, expat's runtests, libxml2's runtest, Little CMS's testcms, HarfBuzz's meson and shaping suites, the Opus and dav1d conformance decodes and brotli's streams. Proposed: WG-07 runs the plan's list plus these, from sources pinned in `scripts/sources.lock`, in the nightly workload job, and Skia's DM and ANGLE's unit tests weekly, because each of their links takes minutes (TC-20).
