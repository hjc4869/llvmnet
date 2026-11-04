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
| ADR-15 | Native extensions of the portable ABI | M10, before 1.0 | Accepted 2026-11-03 (owner, option 4; work pulled before 1.0) |
| ADR-16 | SIMD levels: vector lowering, intrinsic helpers, native kernel packages | M11, before 1.0 | Accepted 2026-11-03 (owner; work pulled before 1.0) |
| ADR-17 | ReadyToRun for JIT deployments | M12 (proposed) | Proposed 2026-11-04 |

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

**Update (2026-11-03).** ADR-16 breaks the deferred general lowering into levels, and the owner moved that work before 1.0 (workstream SV, M11); scalarization remains the behavior of programs that use none of its options.

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

## ADR-15: Native extensions of the portable ABI

**Context.** Portable programs (`dotnet64-v2`) are one image for every host and contain no native code, so they cannot link native libraries; system mode can, but only on Linux x86-64, and it ties the image to that host. Some libraries cannot be recompiled into portable bitcode because they are the operating system's or the GPU vendor's: EGL and OpenGL ES, Vulkan, windowing. The library survey ([2026-11-02](../evidence/2026-11-02-library-survey.md)) showed ANGLE running in portable mode up to exactly this point ("Could not dlopen native EGL"), and the owner asked (2026-11-03) for a well-defined way to reference such standardized libraries with clear FFI boundaries without making the whole application depend on a host ABI, especially on Windows and macOS, where no system mode exists: shared neutral code, plus a short list of proxied interfaces, packaged per platform, with runtime detection. The workstream is [11-native-extensions.md](11-native-extensions.md).

**Options.**

1. *Status quo.* Portable programs call native libraries only through a managed application that wraps them by hand; GPU work needs system mode and Linux. Rejected: it leaves Windows and macOS without a path, and every application repeats the wrapping.
2. *Per-host native bridges, as system mode does.* LLVM generates a bridge library per signature at build time for each target (RID); the image loads the bridge for the host it runs on. Rejected as the primary mechanism: it needs a native toolchain and the host library for every target at build time, produces one bridge per RID to ship and test, and reintroduces the per-target dependency the portable ABI exists to remove. It remains the right answer for ABIs the CLR cannot express (variadic natives, `long double`), which the target APIs do not use.
3. *A generic foreign-function interpreter* (libffi-style call descriptors in the runtime). Rejected: it needs native code per host anyway, interprets every call, and checks nothing.
4. *Generated managed proxies, one assembly per API* (recommended). A generator reads the API's registry (Khronos XML) or headers and writes an assembly of `CExport` stubs with llvmnet's CIL signatures, each calling the native entry point through an unmanaged function pointer with the exact C types; a table of managed stub pointers serves `dlsym` and `eglGetProcAddress`; `[UnmanagedCallersOnly]` thunks serve declared callback types; the loader finds the host library by per-OS names at run time. The CLR applies the host's calling convention, so the assembly is platform-neutral and the image stays one file for every host; `--cil-reference`, `CExport` and the NativeAOT project already carry such assemblies. The runtime gains `dlopen` entry points over a registry of extensions. Extensions live in `sysroot/dotnet64-v2/ext/<name>/` with neutral headers and a manifest, versioned apart from the frozen v2 header set.
5. *Proxies emitted by the compiler from the program's own prototypes.* For a direct call to an undefined function listed by an extension, the compiler could emit the unmanaged call from the LLVM declaration (`zeroext`/`signext` recover the narrow types), with no generated assembly. Not chosen alone: `dlsym`-style loaders need the table of every entry point and callback types need declarations, both of which only the registry or headers provide. Possible later as an optimization of direct calls (NX-11).

**Measurement (2026-11-03).** The spike ([evidence](../evidence/2026-11-03-native-extension-spike.md)) built option 4 for EGL and GL by hand from the Khronos registries (3,458 stubs, an 817 KB assembly) with no compiler or runtime change. A plain GLES 2 program and ANGLE's unmodified GL backend rendered the native pixels from one portable image on Linux x64 (Mesa, JIT and NativeAOT), Windows x64 (ANGLE over Direct3D 11) and macOS ARM64 (ANGLE over Metal); `GL_KHR_debug` callbacks from the drivers reached portable code on all three; `glGetError` costs 8.6 to 8.9 ns under the JIT and 7.4 to 8.4 ns as NativeAOT against 7.3 to 7.6 ns native, about 1 ns per call for the transition.

**Recommendation.** Option 4, with the extension package, driver and runtime support of NX-02 to NX-06, `gles` as the first extension and `vulkan` as the second, and option 2 kept for system mode only. Structures by value (typed CLR structures, NX-07) and callbacks on native threads (NX-08) follow; variadic and C++ interfaces stay outside the mechanism.

**Consequences.**

- The portable ABI contract gains compatible additions only: host entry points for `dlopen` and friends, and an `ext/` directory outside the frozen header set. A program that uses no extension is unchanged, byte for byte.
- A portable image that uses an extension is still one image for every host, with a conditional dependency on a host library that may be absent; the documentation calls this a *portable program with native extensions*, and availability is a program decision at run time, not a build decision.
- The managed surface stays within ADR-09: scalar and pointer `CExport` signatures; typed structures are new and unstable until NX-07.
- Native code runs with the process's rights, as the program does; SECURITY.md says so. An extension loads only the names its manifest lists.
- The compiler and runtime do not grow a per-API feature: each extension is a generated package, and the list of extensions is a packaging decision.

**Status.** Accepted 2026-11-03 by the owner: option 4 (generated managed proxies), system-mode bridges unchanged. The owner also pulled the whole workstream, including NX-02 and NX-03, before 1.0, and later the same day accepted the names as implemented (see the 2026-11-03 section below).

## ADR-16: SIMD levels

**Context.** ADR-10 keeps scalarization and the opt-in `wasm_simd128.h` path for 1.0 and defers "general lowering of `<N x T>` to `Vector128`/`Vector256`" (BE-32, PF-09) to after 1.0 without a design. The library survey ([2026-11-02](../evidence/2026-11-02-library-survey.md)) put numbers on the gap: the shipped Opus, dav1d and libjxl are 2 to 17 times faster than llvmnet's scalar builds, Skia's 8-lane kernels run at 30 to 80 times native once scalarized, and system mode fails at the link on the SSE paths that `__SSE2__` selects (SA-12). The owner asked (2026-11-03) for a multi-level plan covering LLVM's vector IR and autovectorization, the C and C++ intrinsics and the inline and separate assembly of the popular ecosystems, in both runtime modes, keeping today's single-ISA-per-compile libraries working, with extended options and APIs for runtime detection, bundled per-architecture assembly loaded by the JIT or linked statically by NativeAOT, and a C fallback; emscripten's SIMD over WebAssembly as the reference. The survey of what LLVM, Clang and .NET provide is in [2026-11-03-simd-survey.md](../evidence/2026-11-03-simd-survey.md); the workstream is [12-simd.md](12-simd.md).

**Options.**

1. *Status quo.* Scalarization, the SIMD128 header, rejection of target intrinsics and assembly. Rejected: the survey's 10 to 80 times are the difference between usable and not for codecs and 2D graphics, and the ecosystem's SIMD cannot be configured away everywhere.
2. *Platform-neutral vector lowering only* (Levels 1 and 2 with `Vector128` compositions and no ISA-specific paths). Simplest and fully portable, but NativeAOT's default instruction set leaves the opportunistic SSSE3 to SSE4.2 instructions unused (a `Vector128.Shuffle` with run-time indices costs 13 ns against 4 ns through an explicit `Ssse3.IsSupported` branch on the same CPU), and the x86 operations without a neutral equivalent (`psadbw`, `pmaddwd`, `pmaddubsw`, `phminposuw`, `pshufb`) stay 3 to 10 instructions. Kept as the fallback inside option 3.
3. *Vector lowering plus intrinsic helpers with explicit `IsSupported` dispatch* (recommended for Levels 1 and 2). The image stays one platform-neutral file; the JIT folds the checks, ILC compiles them to constants or runtime checks, and every host gets its own instruction where one exists and the exact composition elsewhere. The helpers are what emscripten's SSE and NEON compatibility headers are for WebAssembly, moved from the header into the runtime so that one table serves both the C intrinsics and the `llvm.x86.*` calls the bitcode already contains.
4. *Native kernel packages* (recommended for assembly, Level 3): the project's build assembles its kernels per runtime identifier; llvmnet generates `DllImport` stubs from the bitcode's declarations, loads the running host's library under the JIT and links one identifier's archive statically under NativeAOT, and provides the CPU detection the library's own dispatch needs (`__cpu_model`, CPUID and XGETBV emulation over `X86Base.CpuId`, `getauxval` bits on ARM64) so that the C fallback is selected exactly where no kernel can run. Translating assembly to CIL is rejected (a lifter, and the libraries' `checkasm` would not trust it); running a kernel through the system-mode argument-record bridge is rejected for leaf kernels because a direct P/Invoke is cheaper and works in both modes.
5. *Compiler-generated native kernels* (Level 4, experimental): leaf functions compiled by LLVM's backends into the system-mode sidecar, later into per-identifier packages for portable images with the CIL translation as fallback. Not recommended before a spike (SV-17): it reintroduces native code into portable images and needs a data-layout compatibility check per function.
6. *Make `Vector<T>`-style variable width or scalable vectors the model.* Rejected: `Vector<T>`'s count is fixed at AOT time, SVE is not usable at run time in .NET 10, and LLVM's scalable vectors do not occur for the portable target.

**Measurement (2026-11-03).** The survey evidence file records: the census of the corpus (ffmpeg 185 nasm and 66 AArch64 `.S` files, dav1d 47 and 43, libjpeg-turbo 91 nasm files; intrinsics and vector extensions in libjxl, Opus, libwebp, Skia, brotli, zlib); that Clang lowers 53 of 99 common SSE2 to AVX2 intrinsics to generic IR and 46 to `llvm.x86.*`; LLVM 22's counts (328 x86 intrinsics from SSE to AVX2 and FMA, 723 AVX-512, 209 NEON, 25 WebAssembly SIMD); .NET 10's ISA classes and the NativeAOT instruction-set behavior above; that `scalarizer<min-bits=128>` fragments wide arithmetic; and the timing of hand-written `Vector128`/`Vector256` loops against native scalar and vectorized code for seven kernels, the stand-in for what Level 1 would emit: 3.7 to 5x over today's scalar translation at 128 bits and 7 to 10x at 256 bits on element-wise and widening loops, 2.3 to 3.9x on float dot products, level with native SSE2 and AVX2 except on the `psadbw` and `pmaddwd` idioms (1.4 to 1.6x, closed by the x86 intrinsics behind `IsSupported`), while today's scalarized vectorization costs 1.2 to 2x.

**Recommendation.** Options 3 and 4 as Levels 1 to 3 of [12-simd.md](12-simd.md), in that order, each with a measured exit; option 5 as a spike after Level 3. Autovectorization is a consumer of Level 1 and its default (ADR-11) is decided by measurement in SV-05, not here. The `--isa` option carries the single-ISA-per-compile baseline; multi-ISA bundles and NEON source compatibility are P2.

**Consequences.**

- The data ABI is unchanged at every level: `--isa` changes instructions, macros and vectorizer features only, and the ABI freeze test keeps checking the layouts. Objects of different ISA levels link together.
- A program whose IR has no vector operations is byte-identical to today's until SV-05 changes the vectorizer default, which is a separate owner decision; vector IR that arises without the new options (the sysroot's complex, locale and OpenMP members, Clang's vector extensions, SROA, the x86-64 ABI's two-float pairs) is lowered by Level 1, and no program of the benchmark set may be slower for it (reworded by the owner on 2026-11-03).
- Portable images stay one file for every host through Levels 1 and 2. Level 3 adds native libraries the project's build produced, per identifier, next to the image, and the program's C paths remain the fallback; Level 4 would make llvmnet itself a producer of native code and is decided separately.
- `LlvmNet.Runtime` grows a helper table (a few hundred small methods) that must be exact to the instruction; the conformance test against the native instruction on x64 is part of the definition of done.
- System mode's SSE macro question (SA-12 option (b)) stays the owner's; Level 2 makes option (c) real and removes the failure the survey found for the common intrinsics.

**Status.** Accepted 2026-11-03 by the owner: Levels 1 to 3 as proposed, a spike (SV-17) before any decision on Level 4. The owner also pulled the workstream before 1.0 (see the 2026-11-03 section below).

## ADR-17: ReadyToRun for JIT deployments

**Context.** Under the JIT, a translated function too large for RyuJIT's limits runs as MinOpts code. The limits are 60,000 bytes of IL, 20,000 IL instructions, 2,000 blocks, 2,000 locals and 8,000 local references, and they are constants in release builds. Large C functions reach them: 49 of ffmpeg's decoder methods, including H.264's CABAC decoder and deblocking filters, so H.264 runs at 2.0 times native under the JIT against 1.46 as NativeAOT. Short programs pay for compilation instead: jpeg, lame and tramp3d run at 5 to 36 times native under the JIT (PF-06, PF-10). AOT compilation never falls back to MinOpts. A ReadyToRun image keeps the IL, adds code compiled by crossgen2 with RyuJIT, and still tiers hot methods up to Tier1 at run time. For an over-limit method the Tier1 recompilation falls back to MinOpts, and the runtime discards it and keeps the precompiled code. Measured 2026-11-04 ([evidence](../evidence/2026-11-04-jit-patterns.md#readytorun)): the ffmpeg decodes take 27% (H.264) and 15% (HEVC) less time with a ReadyToRun image, at the same hashes. Over 22 benchmarks of the set it takes 0.847 of the JIT's time, from 0.28 (tramp3d) to 1.22 (dhrystone), and it adds up with the tiering setting JP-02 proposes (bullet on 4 cores: 2.63 s by default, 1.89 s without the delay, 1.78 s with both). A linux-x64 ReadyToRun image fails with `BadImageFormatException` on macOS ARM64 and on Windows x64, where the IL-only image runs.

**Options.**

1. *Status quo.* IL-only images under the JIT; NativeAOT where speed matters; outlining (BE-26) for over-limit functions. Outlining is still needed for NativeAOT's code size and the JIT's other limits, but it is large work and does not address compile time.
2. *ReadyToRun in the portable image* for the build host's identifier. Rejected: the image then fails to load on every other host (measured), which breaks R4.
3. *A ReadyToRun output for one runtime identifier* (`--readytorun <rid>`, beside `--nativeaot`), recommended. The portable image is unchanged; a deployment that knows its host gets precompiled code and keeps everything the JIT offers (tiering, reflection, loading extensions). Builds take seconds, against minutes for NativeAOT.
4. *One portable folder with precompiled copies per identifier*, which the host selects through `deps.json` runtime targets with the IL image as fallback. This keeps one deployment for every host but multiplies its size. Whether the host applies runtime targets to the application's own assembly has not been checked; it needs a spike before it can be chosen.
5. *Partial ReadyToRun* (JP-10): only the methods over the JIT's limits are precompiled, so the rest keeps Tier0, on-stack replacement and Tier1's knowledge of initialized classes. It is also specific to one identifier, so it refines option 3 or 4.

**Recommendation.** Option 3 as JP-06, opt-in; option 5 measured as its refinement; option 4 only after a spike.

**Consequences.**

- The toolchain or the offline feed (TC-12) carries the crossgen2 package for each host identifier it supports, as it does ILCompiler for NativeAOT.
- Images grow: ffmpeg's decoder goes from 27 to 45 MB.
- ReadyToRun is not always faster. Precompiled code knows nothing about class initialization, and a loop that never leaves precompiled code is not replaced by on-stack replacement: dhrystone takes 22% longer. The option stays opt-in, and the evidence lists where it loses.
- The data ABI and the portable image are unaffected; the ABI freeze test does not change.

**Status.** Proposed 2026-11-04.

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
- **Implemented as defaults (TC-07, 2026-11-02).** The two TC-07 proposals above and below are built as proposed ([evidence](../evidence/2026-11-02-configure-probes.md)), so they can be accepted or rejected as they work. One consequence concerns SPEC: 621.wrf_s's call to `omp_get_thread_num` through an implicit interface (`float ()` against `i32 ()`), one of the listed exclusions, is a call without arguments through an interface without parameters, so it now compiles to a trap instead of failing the build. Whether the workload reaches the call is unknown until the benchmark is rerun; it stays listed. PALM's four-argument `open` is still rejected. Linking a program against a `SHARED` target's archive relies on TC-19's archives by content (`d4895c9`), merged into the TC-07 branch with the owner's approval.
- **`SHARED` targets and `UNIX` in the CMake toolchain file (TC-07).** `CMAKE_SYSTEM_NAME Generic` keeps host libraries out of the search paths, but also leaves `UNIX` unset and makes every `SHARED` target a configure error. Options for `SHARED`: (a) keep the error and document each project's static-only switch; (b) build such targets as static bitcode archives, which is what zlib's default configuration, libpng's tests and libjpeg-turbo's dual build need; (c) build them as `-shared` CIL libraries, which cannot contain the C library ([linking.md](../user/linking.md)), so most C libraries would fail when run. Proposed: (b), and `UNIX` set without CMake's Unix search paths.
- **Host macros (ADR-14).** The survey's projects needed a setting wherever they choose platform code by host macros: Skia `-DSK_BUILD_FOR_UNIX` (without it Skia builds for macOS), ANGLE, libdrm and googletest `-D__linux__`, brotli `-DBROTLI_BUILD_64_BIT` (without it brotli takes its 32-bit paths, 16% slower), FreeType and libpng `-DUNIX=ON`. Defining `__unix__` alone would have fixed Skia, not the others. Proposed: keep ADR-14 as accepted, remove the x86 macros that are still predefined (TC-22), and list these settings in [troubleshooting.md](../user/troubleshooting.md).
- **x86 feature macros in system mode (SA-12).** System mode is the native x86-64 ABI, for which Clang predefines `__SSE2__` and related macros, but translated code cannot execute x86 intrinsics, so libraries that test the macros fail at the final link. Options: (a) translate the common SSE2 intrinsics to `Vector128` (large, comparable to PF-09); (b) undefine the SIMD feature macros in system mode as portable mode does, so libraries take their generic paths (the data layout does not change; code that requires SSE then fails to compile, as in portable mode); (c) keep the macros and diagnose an intrinsic at compile time. Proposed: (b), with the diagnostic of (c) for code that includes the intrinsic headers regardless.
- **What WG-07 runs.** The plan lists zlib, bzip2, xz, sqlite, lua, zstd, libpng, libjpeg-turbo, expat and pcre2. The survey's suites that compare with native bit for bit, and that found the defects above, are libjpeg-turbo's and libpng's ctest, expat's runtests, libxml2's runtest, Little CMS's testcms, HarfBuzz's meson and shaping suites, the Opus and dav1d conformance decodes and brotli's streams. Proposed: WG-07 runs the plan's list plus these, from sources pinned in `scripts/sources.lock`, in the nightly workload job, and Skia's DM and ANGLE's unit tests weekly, because each of their links takes minutes (TC-20).

## Decided by the owner (2026-11-03, native extensions)

Questions from the [native extension spike](../evidence/2026-11-03-native-extension-spike.md) and the design in [11-native-extensions.md](11-native-extensions.md).

- **ADR-15.** Accepted: generated managed proxies (option 4) are the mechanism for native libraries in portable mode, with system mode's bridges unchanged.
- **Milestone.** Decided: all native-extension work, NX-02 and NX-03 included, lands before 1.0 instead of as a post-1.0 M10. The plan's milestone table and 1.0 exit criteria still need to be updated to match.
- **First extensions and their priorities.** Accepted: `gles` (EGL, GLES, desktop GL) as P1 and `vulkan` as P2; windowing libraries (SDL3, GLFW) only through the C-header generator later.
- **Test dependencies on other hosts.** Accepted: (a) now, the `xp` jobs use an ANGLE present on the runner, found through `LLVMNET_EXT_GLES_DIR`, and skip otherwise; (c), a pinned prebuilt ANGLE fetched by hash, when the extension ships.
- **Names.** Accepted as implemented: `sysroot/dotnet64-v2/ext/<name>/`, `--extension=<name>`, `-l<name>` mapped through the manifest when no archive matches, `LlvmNet.Ext.<Name>.dll` for every extension including third-party ones, `<llvmnet/ext.h>` with `llvmnet_ext_available` and `llvmnet_ext_library`, and `dlopen(NULL)` unsupported until a use case appears. Accepted with them, the names the implementation added: `--list-extensions`, `--install-extension`, `--uninstall-extension` and `--print-extension-kit`; `LLVMNET_EXT_<NAME>_DIR` and `LLVMNET_EXT_<NAME>_NON_BLOCKING`; the `llvmnet.extension` and `llvmnet.extensions` metadata; `manifest.json`, `extension.json` and the kit and template under `share/llvmnet/`; `LlvmNet.Runtime.NativeExtension` and `NativeExtensions.Register`. A second directory searched for extensions, for toolchains installed read-only, stays a possible compatible addition.
- **NX-14.** Deferred: images as compile-time C# references are not an immediate focus; they are done with the full NativeBindings FFI integration, when that integration happens. Until then a host reaches an image's entry point and exports by reflection.

## Decided by the owner (2026-11-03, SIMD)

Questions from the [SIMD survey](../evidence/2026-11-03-simd-survey.md) and the design in [12-simd.md](12-simd.md).

- **ADR-16.** Accepted: typed vector lowering (1), intrinsic helpers with explicit `IsSupported` dispatch (2), native kernel packages with compiler-generated stubs and the detection APIs (3), and a spike before any decision on compiler-generated native kernels (4).
- **Milestone and order.** Decided: the workstream lands before 1.0 (not as a post-1.0 M11), in the proposed order: Level 1 first (SV-02 to SV-07), then Level 2's SSE through SSE4.2 (SV-08) and Level 3 (SV-12, SV-13, SV-15), AVX2 and the P2 items after. The plan's milestone table and 1.0 exit criteria still need to be updated to match.
- **Autovectorization default.** Decided: ADR-11's `-O1` scalar default stands until SV-05 measures the vectorized benchmark set; the choice between Clang's behavior (`-O2` and above vectorize) and an explicit `-fvectorize` is made then.
- **Option names.** Accepted: `--isa=<level>` with `baseline`, `x86-64-v2`, `x86-64-v3`, `wasm-simd128` (alias `--simd128`), `neon`; `--vector-width=128|256`; `--native-kernels=<dir>`; `--report-simd`; the `llvmnet.isa` module flag and image metadata.
- **Portable `-m` flags.** Accepted: the x86 SIMD feature flags (`-msse4.2`, `-mavx2`, `-mfma`, `-march=x86-64-v2/v3`) are aliases of `--isa`, documented as having no ABI effect; other `-m<...>` flags stay rejected.
- **SA-12 under Level 2.** Left open: the helpers make the predefined `__SSE2__` harmless for the common intrinsics in system mode (option (c)); whether to stop predefining the macros (option (b)) is decided after Level 2 results.
- **Native code in portable images.** Accepted: Level 3 ships native libraries next to a portable image when a project opts in (a *portable program with native kernels*). Whether llvmnet itself generates such libraries (Level 4) is decided after SV-17.
- **Test hardware.** Accepted: the build host (AVX-512) covers x64, the `xp` hosts (`fluorite`, `mainmac`) cover Windows x64 and macOS ARM64, and an ARM64 Linux runner is added for the NEON paths.

## Decided by the owner (2026-11-03, SV-02)

Questions from the typed vector lowering ([evidence](../evidence/2026-11-03-vector-lowering.md)), proposed and accepted the same day as recommended: the M11 exit criterion and ADR-16's consequence are reworded as proposed, and the option stays hidden.

- **"Byte-identical without the new options" (M11 exit criterion, ADR-16 consequences).** The criterion assumed that only the vectorizers produce vector IR. The portable sysroot does too: musl's `float complex` functions (`<2 x float>` is their ABI type), libc++'s `locale.o`, libomp's `kmp_atomic.o`, flang-rt's complex `matmul`, `dot-product` and reductions, compiler-rt's `mulsc3` and `divsc3`; and so do Clang's vector extensions, SROA and the x86-64 ABI's coercion of small float structures. Level 1 lowers that vector IR without any option, so every C++ program using iostreams gets different bytes. Measured on the benchmark set: 49 of 54 images and the ffmpeg decoder byte-identical, the other five within noise. Accepted: reword the criterion as "a program whose IR has no vector operations is byte-identical to today's; no program of the benchmark set is slower", and ADR-16's consequence to match.
- **The hidden `--vector-lowering=auto|typed|scalar`.** For comparisons and tests: `typed` keeps every function the lowering translates as vector code (the default `auto` scalarizes those that move more lanes than they compute on), `scalar` scalarizes everything as before SV-02. Accepted: keep it hidden, as `-fslp-vectorize` and the other alias options are, and list it in [dev/architecture.md](../dev/architecture.md) only.

## Proposed for the owner (2026-11-04, JIT patterns)

Questions from the RyuJIT review ([13-jit-patterns.md](13-jit-patterns.md), [evidence](../evidence/2026-11-04-jit-patterns.md)); nothing below has been acted on, and `src/` is unchanged.

- **ADR-17.** Accept ReadyToRun as an opt-in output for one runtime identifier (option 3), with partial images (option 5) measured as its refinement. The alternative is to keep IL-only images and rely on NativeAOT and outlining (BE-26).
- **M12's placement.** Before or after 1.0. JP-02 to JP-05 change only what the backend emits (`runtimeconfig.json` and the CIL). They are small, measured in this review, and independent of ADR-17.
- **The order.** Recommended:
  1. The tiering delay (JP-02): the largest gain for the least change, after the full set is measured on 4 cores.
  2. Switch lowering (JP-03) and `initonly` fields (JP-04).
  3. The per-method budget (JP-05).
  4. ReadyToRun, as ADR-17 decides.

## Proposed for the owner (2026-11-04, SV-05)

Questions from SV-05's measurement of the vectorizers ([evidence](../evidence/2026-11-03-vector-lowering.md#sv-05-autovectorization)) and from BE-33, which it led to ([evidence](../evidence/2026-11-04-be33-local-sharing.md)). Nothing below is changed until the owner decides.

- **Autovectorization at `-O2` and above (ADR-11).** The owner's SIMD decisions left this to SV-05. Measured after this item's fixes: on the benchmark set, which is mostly pointer-chasing integer code, llvmnet's vectorized builds gain about what native's gain (scalar over vectorized time, programs running at least 0.2 s: 1.015 under the JIT and 1.013 as NativeAOT, native 1.021; linpack, perlin and lpbench 1.2 to 1.8 times), and they stand to vectorized native about as the scalar builds stand to scalar native (1.97 and 1.34 against 1.94 and 1.32). On the survey's SIMD kernels the vectorized builds run 2 to 5 times faster than llvmnet's scalar ones, at 1.04 to 2.9 times native SSE2. `-O3` adds nothing for vectorization and can push a function past RyuJIT's IL limit (linpack's `main`, PF-07). The losses are native's own (olden-bh, fftbench) or layout effects (dhrystone under the JIT). Options: (a) Clang's behavior: `-O2` and above run the loop and SLP vectorizers as Clang's do, in both runtime modes, `-fno-vectorize` and `-fno-slp-vectorize` turn them off, and `-O1`, the default, stays scalar as Clang's `-O1` does; (b) keep them off at every level unless `-fvectorize` or `-fslp-vectorize` is given, as today. Recommended: (a), because build systems pass `-O2` and `-O3` expecting Clang's code and the survey's codecs and math libraries are where vectorized loops pay, with Clang's own vectorizer settings (no llvmnet-specific setting measured better). It needs a driver change, the driver reference, [modes.md](../user/modes.md) and the benchmark harness's scalar column, which would then pass `-fno-vectorize` explicitly as today.
- **The vectorized column in the nightly.** SV-05 adds `--vectorized` to the nightly benchmark step, as the item asks. The full run with it took 31 minutes on the build host; on the 4-core runner, whose scalar run takes about an hour, it adds about another hour to a job that also runs every test tag and the fuzzers within its 6-hour limit. Proposed: keep it in the nightly; if the job gets too long, run the vectorized engines weekly instead.
- **M11's byte-identical criterion.** The accepted wording is "a program whose IR has no vector operations is byte-identical to today's". BE-33 changes the locals of every function of more than 512 values, and every program links some (musl's `printf_core`, mallocng's `alloc_slot`), so no image is byte-identical to the images before it. Proposed: judge the criterion against images built by the same compiler with the workstream's options and lowering off (`--vector-lowering=scalar`), which shows that Level 1 leaves scalar code alone, rather than against an older compiler.

## Proposed for the owner (2026-11-04, SV-08)

Questions from SV-08 ([evidence](../evidence/2026-11-04-x86-intrinsics.md)).

- **NativeAOT's instruction set for `x86-64-v2` programs (implemented, for confirmation).** A NativeAOT program whose image records `x86-64-v2` objects now gets ILC's `x86-64-v2` instruction set unless `--aot-instruction-set` is given, so the executable requires an SSE4.2 processor (x64 since about 2009), as the same code compiled natively with `-march=x86-64-v2` does. At ILC's baseline SSE4.2 is an opportunistic set: the helpers test it at run time, but the generic `Vector128` operations of the same functions are compiled for SSE2, and astc-encoder's SSE4.1 build ran 4.41 s against 2.42 s. Options: (a) as implemented; (b) keep the baseline default and document `--aot-instruction-set x86-64-v2`. Recommended: (a); `--aot-instruction-set baseline` still gives an executable that starts on every x64 processor.
- **SA-12's option (b).** The owner left it for after Level 2's results. With the SSE to SSE4.2 intrinsics translated, system mode's predefined `__SSE__` and `__SSE2__` select paths that work, as native x86-64's do, and the survey's three failures run (astc-encoder, Chromium zlib, abseil). Proposed: keep predefining the macros and close SA-12; AVX and later still warn while compiling and fail at the link when reached.

## Proposed for the owner (2026-11-04, SV-09)

Questions from SV-09 ([evidence](../evidence/2026-11-04-avx2-intrinsics.md)).

- **NativeAOT's instruction set for `x86-64-v3` programs (implemented, for confirmation).** By the same rule as SV-08's, a NativeAOT program whose image records `x86-64-v3` objects gets ILC's `x86-64-v3` instruction set, so it requires AVX2, BMI2 and FMA (x64 since about 2015), as native code compiled with `-march=x86-64-v3` does; `--aot-instruction-set baseline` or `x86-64-v2` still gives an executable for older processors, where the helpers test AVX2 at run time. Recommended: as implemented, decided together with SV-08's question.
- **`llvm.fmuladd` rounded once where the target has FMA (implemented, for confirmation).** llvmnet rounded every `llvm.fmuladd` twice. Native code rounds it once in functions compiled for FMA, so libjxl's AVX2 build decoded different pixels than native. Functions whose target features include FMA (`--isa=x86-64-v3`, `-mfma`, `target("fma")`) now get `llvm.fma`, rounded once on every host; others keep two roundings. Options: (a) as implemented; (b) keep two roundings everywhere and document the difference. Recommended: (a), which gives native's results for the code that asks for FMA.
