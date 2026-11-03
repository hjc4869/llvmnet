# llvmnet production readiness plan

This directory is the working plan for taking llvmnet from a prototype that runs ffmpeg and SPEC CPU to a toolchain that other people can depend on. It is written for human review and progress tracking: every piece of work is a checkbox with an ID, a size, a priority and a done condition, grouped into workstreams and sequenced by milestones. The architecture decisions the plan depends on are listed separately so they can be accepted or rejected before work starts.

Related: [README.md](../../README.md), [user/limitations.md](../user/limitations.md), [dev/architecture.md](../dev/architecture.md), [user/modes.md](../user/modes.md).

## How to use this plan

- **Items** live in the workstream files below. Each looks like `- [ ] **<prefix>-<number>** (size, priority) ...` with a done condition. Tick the box and append the PR or commit when done. Add `— blocked: <reason>` or `— descoped: <ADR>` rather than deleting an item.
- **Sizes**: S about a day, M about a week, L several weeks, XL needs its own breakdown before it starts. **Priorities**: P0 blocks 1.0, P1 should ship in 1.0, P2 is a post-1.0 candidate.
- **Milestones** (M0 to M9) have exit criteria that name the items they need. A milestone is done when its exit criteria hold on CI, not when the items are ticked.
- **Decisions** (ADR-01 to ADR-17 in [decisions.md](decisions.md)) are Proposed until the project owner marks them Accepted. Items that depend on a decision say so.
- **Review cadence**: once a week, update the status board below, tick items, and add new items with the next free number in their workstream. Never renumber. If an issue tracker is used, one issue per item with the ID in the title and a label per workstream and milestone.
- **Evidence**: a claim that something works belongs in `docs/evidence/` (dated, produced by CI) or in a test, not in this plan.

## Decisions (2026-10-31)

The owner settled every open decision in [decisions.md](decisions.md): the `dotnet64-v2` tag and ADR-14 option 3 are confirmed, and parity for SPEC means passing with documented per-benchmark flags, except listed exclusions (`fork`, which stays `ENOSYS`; glibc behavior that musl does not share; and prototypes that contradict the definition, accepted 2026-10-31). v2 freezes at 1.0 with `-funsigned-char` allowed (BE-41). ADR-05, 06, 10, 11 and 12 are accepted as recommended. ADR-08 is deferred: no numeric performance target for 1.0. `--native-import` is removed (SA-01). Item-level decisions are recorded on SA-07, LC-24, FT-08, PF-08 and WG-06.

## Progress (2026-11-04, CIL shaped for RyuJIT)

The owner asked whether CoreCLR's JIT is tuned for particular IL patterns, and whether llvmnet could emit them. The design is a new workstream, [13-jit-patterns.md](13-jit-patterns.md) (prefix JP, milestone M12, placement for the owner), with [ADR-17](decisions.md#adr-17-readytorun-for-jit-deployments). Measurements are in [evidence/2026-11-04-jit-patterns.md](../evidence/2026-11-04-jit-patterns.md), from RyuJIT's sources (dotnet/runtime `main`, after `release/11.0`) and scratch builds of the compiler. Nothing in `src/` changed.

- **Tiering delay.** Tier1 promotion waits until 100 ms pass without a first call to any method, and translated programs keep making first calls. `CallCountingDelayMs` 0, a `runtimeconfig.json` property PF-06 did not try, makes 22 benchmarks 9% faster under the JIT (geometric mean): lame by 43%, tramp3d by 27%, bullet by 24%, ffmpeg's H.264 decode by 12%. Four short programs get slower, by 6 to 12% (JP-02).
- **Limits.** RyuJIT compiles a method with MinOpts past 20,000 IL instructions, 60,000 IL bytes, 2,000 blocks, 2,000 locals or 8,000 local references. It inlines nothing into a method with 921 locals or more. In ffmpeg's decoder, 49 methods are over a limit, 48 of them on the instruction count, and 150 cannot inline. Each over-limit method is compiled twice, because its Tier1 attempt is discarded. BE-33's local sharing fixes the locals. With it, 26 methods are still over: on local references, which sharing does not reduce, the instruction count and IL size. `--diagnose-limits` reports 16 of them, as it does not model the first two (JP-05, BE-46).
- **ReadyToRun.** crossgen2 never falls back to MinOpts, and the runtime keeps its code for over-limit methods. A precompiled image runs the H.264 decode 27% faster under the JIT (2.03 to 1.47 times native, NativeAOT's ratio). It also runs 22 benchmarks at 0.85 of the JIT's time, and tramp3d 3.5 times faster. But it is tied to one host: the linux-x64 image fails to load on macOS ARM64 and Windows x64. And it is slower where a hot loop never leaves precompiled code (dhrystone 22%). So it is proposed as an opt-in output for one runtime identifier (JP-06, ADR-17).
- **CIL forms.** llvmnet's compare chain for an LLVM `switch` makes a 40-case interpreter 2.6 to 2.8 times slower than a CIL `switch`, under the JIT and NativeAOT (JP-03). Global address fields marked `initonly` become constants in optimized JIT code: 12% on PF-17's kernel, nothing under NativeAOT (JP-04). Both are neutral on the benchmark set (0.994) except almabench, 3% slower. `cpblk` for small constant copies helps NativeAOT only (JP-08).
- **Found on the way.** RyuJIT never if-converts inside a loop, and integer `Math.Min`/`Max` are not intrinsics on x64 or ARM64; this bears on PF-15 and PF-18. JIT settings come only from the environment. The .NET 11 RC2 runtime runs the subset at 10.0.14's speed.
- **Proposed for the owner.** ADR-17; M12's placement; the order of JP-02 to JP-05, which change only what the compiler emits ([decisions.md](decisions.md#proposed-for-the-owner-2026-11-04-jit-patterns)).

## Progress (2026-11-04, SV-05, SV-07 and BE-33)

Measurements are in [the vector lowering evidence](../evidence/2026-11-03-vector-lowering.md#sv-05-autovectorization) and [evidence/2026-11-04-be33-local-sharing.md](../evidence/2026-11-04-be33-local-sharing.md).

- **SV-05 (measured, decision pending).** The benchmark harness builds the vectorized engines on request (`--vectorized`, also in the nightly). The first run made llvmnet 9 to 12% slower vectorized than scalar; the causes were RyuJIT leaving `Vector128` methods uninlined in methods of about 920 locals, functions past the 1,024 locals RyuJIT keeps in registers, and lane inserts through memory without SSE4.1, and all three are fixed. Vectorized llvmnet builds now gain about what native ones gain on the benchmark set and run the survey's SIMD kernels 2 to 5 times faster than scalar ones. Whether `-O2` and above vectorize as Clang's do is proposed to the owner ([decisions](decisions.md#proposed-for-the-owner-2026-11-04-sv-05)); the library reproducers are still to be measured.
- **SV-07 (done).** The SIMD128 header is written over Clang's vector extensions, as Clang's own is, with helpers only for the `llvm.wasm.*` operations, and `--simd128` is now `--isa=wasm-simd128` on the typed lowering. Its tests pass unchanged, and ffmpeg's SIMD128 HEVC profile is faster in every engine, by a fifth as baseline NativeAOT, where it now beats the scalar profile instead of trailing it.
- **BE-33 (locals done, IL density open).** Functions of more than 512 values share IL locals between values whose live ranges do not meet. ffmpeg's decoder has no method left past RyuJIT's locals threshold (38 before), its IL is a fifth smaller, H.264 decoding is 10 to 17% faster, and the scalar benchmark set is 1 to 2% faster overall. Smaller functions keep a local per value: RyuJIT allocates registers per local, and sharing cost small hot loops moves. A side finding, RyuJIT storing a spilled loop bound back on every iteration, is PF-20.

## Progress (2026-11-03, PF-19)

Measurements are in [evidence/2026-11-03-pf19-vectored-io.md](../evidence/2026-11-03-pf19-vectored-io.md).

- **PF-19 (found by the 2026-11-02 nightly).** The nightly on GitHub CI failed fwrite at 6.76 times its baseline under the JIT and 9.13 as NativeAOT, while this host matched the baseline. The host layer split every `readv` and `writev` into one host call per vector, so each musl stdio flush cost two writes and each refill two reads. Vectored transfers of up to 64 KiB are now one host call through a pooled buffer, which halves fwrite's host calls (one write per flush, reads at glibc's count) and makes fwrite 4 to 10% faster on this host. It also restores Linux semantics the split broke: pipe writes of at most `PIPE_BUF` bytes are atomic, non-blocking ones return `EAGAIN` instead of a partial count, and a pipe `readv` returns what the pipe holds. The core and toolchain gate and libc-test are unchanged; the next nightly confirms the runner.

## Progress (2026-11-03, SIMD Level 1 core)

The owner asked to start implementing [12-simd.md](12-simd.md). SV-01 is done (ADR-16 accepted) and SV-02 is done ([evidence](../evidence/2026-11-03-vector-lowering.md)): LLVM vector IR of 8- to 64-bit integer, pointer, `float`, `double` and `i1` lanes becomes `Vector128<T>` code (`VectorEmitter.cs`), wider vectors 128-bit fragments, masks all-ones lanes, at every optimization level including `-O0`, which never translated before; functions the lowering does not translate, or whose vectors mostly move lanes (the x86-64 ABI's two-float pairs, SROA's small arrays), are scalarized per function as before. Clang's vector extensions and the vectorizers' output match native in both modes under the JIT, without hardware intrinsics and as NativeAOT, on Linux x64, Windows x64 and macOS ARM64 and under the Linux ARM64 JIT; the scalar benchmark set is unchanged (49 of 54 images byte-identical, five within noise). Two findings changed the design text: LLVM's `scalarizer<min-bits=128>` does not split comparisons, so the backend fragments wide vectors itself, and NativeAOT makes no tail call through a function pointer that returns a `Vector128`, so small vector results return as bytes. The owner accepted both proposals that followed ([decisions](decisions.md#decided-by-the-owner-2026-11-03-sv-02)): M11's "byte-identical" criterion now applies to programs without vector operations, because the sysroot (musl's complex functions, libc++'s locale, libomp, flang-rt) carries vector IR, and the `--vector-lowering` option stays hidden.

## Progress (2026-11-03, native extensions implemented)

The owner accepted ADR-15 and asked for the NX items. NX-01 to NX-12 are done ([11-native-extensions.md](11-native-extensions.md), [evidence](../evidence/2026-11-03-native-extensions.md)): `dlopen` over registered extensions, extension packages with `--extension` and `-l` names, the generator for Khronos registries and C headers, the `gles` and `vulkan` extensions, structures by value through C wrappers, callbacks on threads the host library started, `SuppressGCTransition` for non-blocking entry points, and `--install-extension` with both packages shipping the extensions, a build kit and a template for third-party ones. The M10 exit checks hold locally: images without extensions are byte-identical to the previous ones, the ABI freeze test is unchanged, the core and toolchain gate passed 107 of 107, and the `xp` bundle's extension fixtures pass on Windows x64, macOS ARM64 and Linux ARM64; the CI jobs need FD-19. NX-13, the AvaloniaStatic scenario, is done on Linux x64, macOS ARM64 and Windows x64 (the Windows window run by the owner in the console session, since DXGI refuses a swap chain over SSH): Skia's GL backend, built in portable mode, draws through EGL into a window a .NET host owns, also with the host as one NativeAOT executable. The scenario added ANGLE's own extensions to `gles` (1.1.0) and NX-14, which the owner deferred to the NativeBindings FFI integration. The owner accepted ADR-15's names as implemented ([decisions](decisions.md#decided-by-the-owner-2026-11-03-native-extensions)).

## Progress (2026-11-03, SIMD levels)

The owner asked for a multi-level SIMD plan: LLVM's vector IR and autovectorization, the C and C++ intrinsics and the inline and separate assembly of the popular ecosystems, in both runtime modes, keeping single-ISA-per-compile libraries working, with extended options and APIs for runtime detection, per-architecture assembly bundled for the JIT or linked statically by NativeAOT, and a C fallback, with emscripten's SIMD over WebAssembly as the reference. The design is a new workstream, [12-simd.md](12-simd.md) (prefix SV, milestone M11, before 1.0), and [ADR-16](decisions.md#adr-16-simd-levels): Level 1 lowers vector IR to typed `Vector128<T>` (and `Vector256<T>` on request), which serves the vectorizers, GCC vector extensions and the half of the x86 intrinsics Clang lowers generically; Level 2 translates the `llvm.x86.*`, `llvm.wasm.*` and NEON intrinsics through runtime helpers with explicit `IsSupported` dispatch and exact portable fallbacks, selected by a new `--isa` option that changes no data layout; Level 3 bundles natively assembled kernel packages per runtime identifier with compiler-generated stubs, the project's own C dispatch, and CPU detection APIs (`__cpu_model`, CPUID emulation, `<llvmnet/cpu.h>`); Level 4 (a spike) would let llvmnet compile leaf functions natively. The survey behind it ([evidence](../evidence/2026-11-03-simd-survey.md)) measured the corpus's SIMD forms, which intrinsics Clang keeps target-specific (46 of 99 common ones), what the vectorizers emit for the portable target, .NET 10's SIMD surface and NativeAOT's instruction-set behavior (opportunistic sets help only explicit `IsSupported` checks: 13 ns against 4 ns for a run-time shuffle), LLVM's scalarizer as a 128-bit legalizer, and hand-written `Vector128` loops as a stand-in for Level 1 against native scalar and vectorized code (3.7 to 5x over today's scalar translation on element-wise and widening loops, about double at 256 bits, level with native SSE2 except on the `psadbw`/`pmaddwd` idioms, while today's scalarized `-O3 -fvectorize` costs 1.2 to 2x). Two scalar findings became PF-17 (global base addresses reloaded on every access) and PF-18 (the branch-free select on a loop-carried maximum, 7x native). Nothing is implemented; the owner accepted ADR-16 and pulled the workstream before 1.0 ([decisions](decisions.md#decided-by-the-owner-2026-11-03-simd)).

## Progress (2026-11-03, native extensions of the portable ABI)

The owner asked how a portable program could reference standardized native libraries (EGL and OpenGL ES, Vulkan) through clear FFI boundaries without depending on a host ABI, so that the shared code stays neutral and runs on Windows and macOS, where no system mode exists. The design is a new workstream, [11-native-extensions.md](11-native-extensions.md) (prefix NX, milestone M10, before 1.0), and [ADR-15](decisions.md#adr-15-native-extensions-of-the-portable-abi): generated managed proxy assemblies per API, platform-neutral, loading the host's library at run time, with `dlopen` host entry points in the runtime and extension packages under the sysroot. A feasibility spike ([evidence](../evidence/2026-11-03-native-extension-spike.md), `artifacts/native-ext/`) built such a proxy for EGL and GL from the Khronos registries without changing the compiler or runtime: a plain GLES program and ANGLE's unmodified GL backend render the native pixels from one portable image on Linux x64 (Mesa, JIT and NativeAOT), Windows x64 (ANGLE over Direct3D 11) and macOS ARM64 (ANGLE over Metal), driver callbacks reach portable code, and a call costs about 1 ns more than natively. The owner accepted ADR-15, pulled the workstream before 1.0, and settled the first extensions and how the other hosts' tests obtain ANGLE; the names were accepted later the same day ([decisions](decisions.md#decided-by-the-owner-2026-11-03-native-extensions)).

## Progress (2026-11-02, LLVM built with llvmnet)

An experiment outside the repository (`artifacts/selfhost/`, like the library survey) built LLVM, Clang and Flang 22.1.8 with llvmnet in portable mode: every LLVM tool llvmnet runs (`clang-22`, `flang-22`, `opt-22`, `llvm-ar-22`, `llvm-nm-22`) became a .NET program, under the JIT and as a NativeAOT executable. Replaying the build's 3,542 compile commands with the built `clang-22` gave the same IR as native Clang, and llvmnet's sysroots rebuilt by those tools match master's.

- **BE-47 (done).** Programs with more than 65,535 methods (Clang 119,681, Flang 234,651) linked but did not start, because the CLR limits the methods of one class. Internal methods now go to several classes, 32,768 per class ([evidence](../evidence/2026-11-02-method-holders.md)).
- **Other findings, in progress on their own branches.**
  - The launcher's `argv[0]` and `/proc/self/exe` (TC-21).
  - `llvm.frameaddress`.
  - Precompiled headers.
  - NativeAOT's 32 MB metadata limit for Flang-sized programs.
  - Running the compiler's own in-process LLVM as llvmnet-built code.

## Progress (2026-11-02, library survey fixes)

The fixes for the survey's findings are merged. Measurements are in [link-scaling](../evidence/2026-11-02-link-scaling.md), [configure-probes](../evidence/2026-11-02-configure-probes.md), [musttail-select](../evidence/2026-11-02-musttail-select.md) and [system-mode-survey](../evidence/2026-11-02-system-mode-survey.md); PF-16 has its own entry below.

- **TC-19 and TC-20 (done).** A `main` in an archive member is linked, and archives are recognized by their content under any name. One LLVM linker serves the whole link: Skia's DM links in 23 s instead of 420 s, cjxl against the sysroot in 1.7 s instead of 25 s, with byte-identical translated programs.
- **TC-07 (progress).** Configure probes give a native build's answers in autotools, Meson, CMake and ffmpeg's configure, and the CMake platform `LlvmNet` sets `UNIX` and builds `SHARED` targets as bitcode archives. CMake configures run 6 to 8 times faster with TC-20.
- **BE-44 (done).** `musttail` calls are CIL tail calls and run in constant stack under the JIT and NativeAOT.
- **PF-15 (in progress).** Integer selects, `abs`, min and max compile without branches. The Paeth reproducer went from 6.0 to 1.4 times native, short of the 1.3 target; in the decoders the Paeth filter is 1.18 times native in libpng and 0.95 in Wuffs, and the benchmark set does not regress.
- **SA-10 and SA-11 (done), SA-12 (in progress).** Under the JIT, system-mode `fork` fails with `ENOSYS`, because CoreCLR's W^X shares generated code with a forked child; NativeAOT keeps the host's `fork`. C++20's libstdc++ members link. Untranslated x86 intrinsics warn at compile time.
- **Proposed for the owner.** The TC-07 proposals are implemented as defaults and can be accepted or rejected as they work; system mode's SSE macros (SA-12, the evidence recommends option b); whether PF-15's 1.4 times on the reproducer is enough, given the decoders' 0.95 to 1.18.

## Progress (2026-11-02, PF-16)

Measurements are in [evidence/2026-11-02-pf16-host-memory.md](../evidence/2026-11-02-pf16-host-memory.md).

- **PF-16.** Portable mode's `mmap` emulation takes small mappings from slabs and finds them through page tables. A one-page mapping costs one page, and unmapping is linear in any order. libxml2's DOM parse is 2.7 times faster on Linux with 42% less memory, 5 times faster on macOS and 3 times on Windows, and now beats system mode. The allocation-heavy olden benchmarks run 1.2 to 1.9 times faster, and the benchmark harness shows no regression. Releasing pages discarded with `MADV_DONTNEED` would need an operating-system call and stays undone.

## Progress (2026-11-02, library survey)

Measurements are in [evidence/2026-11-02-library-survey.md](../evidence/2026-11-02-library-survey.md).

The survey built 18 open-source libraries in portable mode through their own build systems:
- the third-party libraries of LightPlayer-Packages;
- AvaloniaStatic's native dependencies, including Skia with the SkiaSharp C API and ANGLE.

Every library ran its own suite under the JIT, compared with a native Clang build, and every comparable output was byte-identical. The only exception is musl's `libm`, which rounds last bits differently in 6 Skia images. NativeAOT runs scalar C at 1.1 to 3 times native time, typically 1.4 to 2.2. ANGLE draws on the host GPU in system mode.

- **WG-07 and TC-07 (progress).**
  - zlib, libpng, libjpeg-turbo and expat pass their suites locally, through local scripts that are not yet in CI.
  - Configure probes give wrong answers in all three build systems, so features drop out silently. The CMake toolchain file's `Generic` platform hides `UNIX` and makes `SHARED` targets an error.
  - Fixed in TC-07 with the owner proposals as defaults ([evidence](../evidence/2026-11-02-configure-probes.md)): probes give native answers in autotools, Meson, CMake and ffmpeg's configure; the CMake platform sets `UNIX` and builds `SHARED` targets as bitcode archives; `-DLLVMNET_RUNTIME` selects the mode.
- **New items.**
  - TC-19: `main` in an archive member, and archives under any name.
  - TC-20: link time grows super-linearly, for example 371 s for Skia's DM.
  - TC-21: `argv[0]` of launcher programs.
  - TC-22: x86 macros still predefined in portable mode.
  - TC-23: trap-mode sanitizers.
  - BE-44: `musttail` is translated as an ordinary call.
  - BE-45: `invoke @setjmp`.
  - BE-46: gaps in `--diagnose-limits`.
  - PF-15: `select` becomes branches, making the Paeth filter 3.4 to 5.7 times slower.
  - PF-16: the portable `mmap` emulation, which makes libxml2's DOM parse 2.4 times slower than system mode with twice the memory.
  - SA-10 to SA-12: system-mode fork and exec, C++20 libstdc++, `__SSE2__`.
  - LC-32: musl differences.
- **Proposed for the owner.** The item priorities, the configure-probe and toolchain-file choices, the host-macro settings, system mode's SSE macros, and what WG-07 runs ([decisions.md](decisions.md#proposed-for-the-owner-2026-11-02-library-survey)).

## Progress (2026-11-01, M9)

Measurements are in [evidence/2026-11-01-m9-workload-gates.md](../evidence/2026-11-01-m9-workload-gates.md), and [dev/gates.md](../dev/gates.md) says which suites run when, with their thresholds. Every suite counts a test only when the native build passes it.

- **WG-05.** The nightly runs ffmpeg's threaded and SIMD128 profiles in both ABIs under the JIT and NativeAOT; the threaded test now checks NativeAOT by default.
- **WG-02.** c-testsuite: 219 of 220 in portable mode (a contradicting `strlen` prototype), 220 in system mode. gcc-c-torture, from the pinned llvm-test-suite: 1,473 of 1,488 portable, 1,475 system, every failure classified.
- **WG-01 (in progress).** llvm-test-suite through its own CMake and lit: portable 2,002 of 2,049 (97.7%; 99.1% without x86 tests and the owner's exclusions), system 2,013 (98.2%; 98.8%), a NativeAOT subset 140 of 141; no unclassified failure. The 99% target holds in portable mode, not yet in system mode.
- **WG-03.** The library suites run in their own nightly job against thresholds; flang-rt's own unit tests pass 216 of 216 in portable mode, and a libc++ subset as NativeAOT 327 of 328.
- **WG-04.** FATE against the real `ffmpeg` and `ffprobe`: the portable build passes all 5,285 tests the native build passes, after the harness patched configure's function probe, which portable mode rejects.
- **WG-09.** The gates page, with thresholds at each suite's lowest group rate rounded down ([proposed for the owner](decisions.md#proposed-for-the-owner-2026-11-01-m9)).
- **WG-06 (open).** No new SPEC runs: the `train` and `ref` matrices under the JIT and NativeAOT need the private runner (FD-21).
- **Found by the suites, fixed with tests.** CMake could not identify llvmnet; `.obj` inputs were rejected; portable `-dumpmachine` reported the host; tied-output `asm` barriers, K&R `main(argc)` and min/max vector reductions did not compile (BE-43); portable `open` with `O_CREAT` and read-only access failed; system-mode K&R calls to native functions produced invalid IL; system-mode `rand()` was seeded by the .NET runtime; flang-rt reported `CMDSTAT` 7 for successful commands on musl. A conservative stack-scanning collector (SIOD) is now a documented limitation, and BE-42 records x87 encodings read by value.
- **1.0 checklist.** RL-02 (the frozen ABI, now documented and tested), RL-05, RL-08 (security and support policy, terms proposed for the owner) and RL-09 are done. RL-01, RL-03, RL-04 and RL-07 wait for the repository on GitHub (FD-19) or a release candidate, RL-06 for TC-18's examples, RL-10 for the tag.
- **M9 exit.** Not reached: WG-01 in system mode, WG-06, and two consecutive nightlies on a release candidate, which need GitHub.

## Progress (2026-11-01, M8)

Measurements are in [evidence/2026-11-01-m8-performance.md](../evidence/2026-11-01-m8-performance.md). There is no numeric target (ADR-08); M8 publishes numbers and guards them. Over the 57 benchmarks, the M8 items took the geometric mean from 2.42 to 2.11 times native under the JIT and from 1.72 to 1.45 as NativeAOT; that run is the new baseline.

- **PF-01.** `scripts/benchmark.py` runs 58 benchmarks (11 microbenchmarks, 42 free `llvm-test-suite` programs, the ffmpeg decode harness on two generated 1080p clips at one and four threads, and a startup probe) natively, under the JIT and as NativeAOT from the same sources, checks every output against native and records medians, user time and peak RSS. Baseline on this host: JIT 2.42 and NativeAOT 1.72 times native (geometric mean). The nightly job fails when a benchmark gets 25% slower or a geometric mean 10% slower than `tests/perf/baseline.tsv`, judged on each benchmark's fastest run, normalized by host speed and confirmed by a second measurement; the set, the thresholds and the baseline policy are proposed for the owner in [decisions.md](decisions.md#proposed-for-the-owner-2026-11-01-m8). The first GitHub run of the job waits for FD-19. Building the set found and fixed a host bug: `rewind(stdin)` on a redirected file did nothing.
- **PF-02.** The four-thread ffmpeg decode is not serialized by atomics or locks: as NativeAOT it scales from one to four threads as native does (2.7 and 3.3 times), and under the JIT the shortfall is the half second of startup and compilation before the threads run compiled code. Contended locking was still slow where exercised directly, so futexes now follow the kernel's design (hashed buckets of waiters, exact wake counts, real requeue) and aligned atomics are emitted inline: the mutex microbenchmark went from 2.34 to 1.37 times native, atomics from 1.94 to 1.20 (NativeAOT at native speed).
- **PF-13.** Function addresses are registered for the runtime once, in the module initializer, instead of at every evaluation: the callback microbenchmark is 1.31 times native under the JIT (from 1.98) and at native speed as NativeAOT.
- **PF-03.** Portable streams get 4,096-byte buffers, glibc's size, instead of musl's 1,024, and `long double` comparisons no longer build `BigInteger` values: fwrite is 1.83 times native under the JIT (from 2.39) and 1.41 as NativeAOT (from 2.11). A host call costs 30 to 90 ns more than the kernel's own under the JIT.
- **PF-04.** Translated functions with stack buffers no longer zero them on entry (an alloca's contents are undefined), and allocas of functions with variable-length arrays or `setjmp` come from a per-thread arena instead of one heap block each: H.264 decoding went from 3.20 to 2.20 times native under the JIT and from 2.23 to 1.46 as NativeAOT, varargs and printf by a quarter. HEVC and SPEC deepsjeng gain little.
- **PF-05.** `calli`, which llvmnet emits for indirect calls, is faster than delegates, interfaces or a `switch` over direct calls; ffmpeg's 9 to 24 million indirect calls per decode cost milliseconds, so nothing changes.
- **PF-10.** A JIT hello world starts in 36 ms (from 39; any `dotnet program.dll` takes 17), NativeAOT in 2 ms like native. Invariant globalization turned out to break IANA time-zone names on Windows: the JIT keeps ICU, and NativeAOT executables for Windows, which had resolved `Europe/London` to UTC, keep it now too.
- **PF-11.** NativeAOT uses 7 to 12 MB more memory than native; the JIT adds its runtime and generated code (28 MB, and 60 to 150 MB for large programs). GC settings make no difference to translated programs, whose data lives in native memory.
- **PF-08.** The NativeAOT baseline (SSE2) has no `pshufb`, so the SIMD128 profile's shuffles run in software and it decodes more slowly than the scalar profile; `--aot-instruction-set x86-64-v3` (opt-in, as decided) makes the two equal. Whether `x86-64-v2`, which already has `pshufb`, should become the default for SIMD128 builds is proposed for the owner.
- **PF-14.** `long double` arithmetic computes in 64- and 128-bit integers instead of `BigInteger` values, with the same results bit for bit: the Float80 microbenchmark went from 263 to 62 times native under the JIT and from 227 to 31 as NativeAOT, and printf, whose `%f` formatting computes in `long double`, from 5.2 to 2.2 times native. Pseudo-zeros are zeros in every operation now (dividing by one threw), and out-of-range conversions to `i128` saturate instead of throwing.
- **PF-06 and PF-07 (in progress).** Rotates compile to the JIT's intrinsics (salsa20 as NativeAOT went from 8.0 to 1.05 times native) and dynamic PGO is off in `runtimeconfig.json` (6.6% faster). Over-limit functions fall back to MinOpts only in ffmpeg's H.264 decoder within the free set, costing it about a fifth of its JIT time; outlining them (BE-26) and a full review of JIT code remain.
- **Found by the nightly thresholds.** The first full run after PF-04 flagged lpbench 2.6 times slower under the JIT: without `localsinit` the JIT's on-stack replacement code is slower, so only methods with `localloc` drop the flag now.
- **M8 exit.** PF-01 and PF-02 are done and the benchmark table is published; the exit's nightly table on CI starts with the first GitHub run (FD-19).

## Progress (2026-10-31, M7)

Measurements are in [evidence/2026-10-31-m7-toolchain.md](../evidence/2026-10-31-m7-toolchain.md). Every M7 exit item is done; the signed packages the exit also asks for are built and verified locally and will be signed by the release workflow once the repository is on GitHub (FD-19).

- **SA-01.** `--native-import` is removed; `--system-library` is the one native path in system mode, and the option fails with that guidance.
- **TC-01.** The driver's options are one table with four treatments per runtime mode (handled, passed to the frontend, accepted without effect, rejected with a reason), from which `--help` and the tables of `docs/user/driver.md` are generated; every option Clang's and Flang's `--help` list works or fails with a clear error in both modes (`tests/driver-options.sh`).
- **TC-02 to TC-05.** Link inputs (`-c` on IR and objects, `-l:`, whole archives, order-independent archive resolution, duplicate definitions naming both files, `-r` of archives, the `-nostdlib` family), the `-O1` default with every level tested against native and JIT-validated at `-O0` and `-O3`, `-g`, `-fno-exceptions`, `-fno-rtti`, `-fvisibility` (a library now exports only what a native one would), per-alias language defaults, and a diagnostics pipeline that passes the frontends' output through byte for byte and gives llvmnet's own errors Clang's form and colors.
- **TC-13 and TC-11.** The bundled toolchain drops Clang's sanitizer and profiling runtimes (609 MB to 533 MB installed; 100 MB compressed, 33 MB thin). `scripts/release-artifacts.sh` builds the two archives, the dotnet tool package and the sums reproducibly, and `.github/workflows/release.yml` runs the gate on a tag, builds twice, signs with cosign's keyless signing and creates the release; the signing and the release itself wait for the repository on GitHub (FD-19).
- **TC-16.** `docs/` is `docs/user/` (getting started, driver, runtime modes, linking, deployment, limitations, troubleshooting, support matrix, SIMD128), `docs/dev/` (architecture, host ABI, calling convention, C++ bridge, versioning, testing, adding a host function, release process, artifacts, SPEC, ffmpeg, video benchmark) and `docs/evidence/`; `README.md` is written for users, with every claim pointing at a test or an evidence file; the old `toolchain.md`, `status.md`, `roadmap.md` and SPEC pages are gone and every link in the repository resolves (0 broken of the relative Markdown links).
- **TC-17.** `docs/user/limitations.md` is generated from the support matrix and from the runtime's own table of the system calls it refuses by name (16 services, each with its errno and reason), which the dispatcher now consults; `tests/limitations.sh` keeps the page current and checks the errno values.
- **TC-12.** NativeAOT provisioning: an offline NuGet feed (`scripts/nativeaot-feed.sh`, `LLVMNET_NUGET_FEED`) and a prerequisite check that names the missing SDK, compiler, zlib, cross sysroot or feed package in one line before `dotnet publish` runs.

## Progress (2026-10-31, M6)

Measurements are in [evidence/2026-10-31-m6-cross-platform.md](../evidence/2026-10-31-m6-cross-platform.md), taken on a Windows 11 x64 machine, an Apple M6 Mac and an ARM64 container.

- **M6.** XP-01, XP-02, XP-03, XP-05, XP-06 and XP-08 are done. A cross-platform bundle (`tests/xp/`, 51 fixtures in C, C++ and Fortran, 9 of them also as NativeAOT projects) is built on Linux with the native results recorded, and reproduces them on Windows x64, macOS ARM64 and Linux ARM64 under the JIT and as NativeAOT executables. macOS and ARM64 needed no runtime change. Windows needed a program-visible path form for drives (`/c/...`), directory links, errno mapping, `sh.exe` for `/bin/sh` and a UTF-8 console; what NTFS cannot express (mode bits) is documented. Unsupported hosts print one actionable line (verified on a 32-bit runtime), and `runtimeconfig.json` pins the .NET 10 band. `--aot-project` writes relocatable NativeAOT projects for publishing on other hosts, and linux-arm64 cross-publishes from Linux x64. Found on the way: every image depended on OpenSSL for 16 random bytes at startup (fixed, XP-07). CI has `xp` jobs on Windows, macOS and Linux ARM64 runners; the exit's two weeks of green runs start with the first GitHub run (FD-19).

## Progress (2026-10-31, closing M3 to M5)

Measurements are in [evidence/2026-10-31-m3-m5-closeout.md](../evidence/2026-10-31-m3-m5-closeout.md).

- **M3.** LC-09 is done: where the host has no zoneinfo, the host layer serves TZif images generated from `TimeZoneInfo` (45,056 conversions in 13 zones compared with native). LC-28 closes with it. The remaining M3 exit question is SPEC parity for 507.cactuBSSN_r, which needs glibc's `sprintf` behavior (owner decision, below).
- **M4.** CX-06 is done: libc++'s localization suite passes 261 of 261 once the lit configuration stops advertising the named locales musl does not have; input.output stays at 97.8% because 12 of its 13 failures need FIFOs, Unix sockets, hard links or race-safe `openat`, which .NET cannot provide (LC-30). SPEC C++ parity: 767.nest_r passes with a per-benchmark setting; 766.femflow_r passes with SPEC's own switch for the C++17 special math functions once the `long double` intrinsics it then needs are lowered (BE-37, done); 737.gmsh_r and 748.flightdm_r need glibc behavior no flag selects.
- **M5.** FT-08 is done: LLVM's OpenMP runtime is compiled into the sysroot (`ports/openmp`), `-fopenmp` works for C, C++ and Fortran under JIT and NativeAOT, and 22 of the 25 OpenMP SPEC speed entries pass in portable mode; 627.cam4_s needed two host fixes on the way (thread stacks sized by `pthread_attr_setstacksize`, and the real `RLIMIT_STACK`). The others: 607.cactuBSSN_s (glibc `sprintf`), 822.palm_s and 621.wrf_s (prototypes that contradict the definition, which llvmnet rejects). Along the way: aggregates through variadic calls follow the System V classification (BE-19), and the `long double` math intrinsics become C library calls (BE-37), 184 of 212 support-matrix constructs.
- **SPEC in portable mode.** 91 of the 101 CPU 2017 and CPU 2026 entries pass (67 after LC-31). The 10 others: 3 need `fork` (excluded), 4 rely on glibc behavior (cactuBSSN rate and speed, gmsh, flightdm) and 3 have prototypes that contradict the definition (palm rate and speed, wrf_s).
- **Owner decision (2026-10-31).** Programs that fail natively against musl too (cactuBSSN, gmsh, flightdm) and prototypes that contradict the definition (palm, wrf_s) are ADR-14 parity exclusions, recorded in [decisions.md](decisions.md#adr-14-linux-identity-of-the-portable-target). Portable SPEC parity therefore holds for every entry, and the M3, M4 and M5 exits are complete.
- **Still open from M0 and M1.** The first GitHub run of the PR gate (FD-19) and the two-week fuzzing criterion (FD-16) need the repository on GitHub; everything else in the M0 to M2 exits is done.

## Progress (2026-10-30, M3 to M5)

Work on the `m3-m5` branch replaced the hand-written runtimes with the upstream ones in both modes. Measurements are in [evidence/2026-10-30-m3-m5.md](../evidence/2026-10-30-m3-m5.md).

- **M3 portable C library.** Portable mode (the default, tag `dotnet64-v2`) compiles against and links unmodified musl 1.2.5 from `sysroot/dotnet64-v2`, built by `scripts/build-sysroot.sh`. The host layer implements Linux system-call semantics over .NET with a documented contract (`docs/dev/host-abi.md`, including every runtime call into translated code, LC-18) and a conformance test against native. `managed-host`, the hand-written C library and MathNet.Numerics are deleted (LC-17). libc-test passes 385 of 403 natively passing tests, every failure a documented limitation; printf/scanf/strtod (630 cases), libm (89 functions on random inputs, bit-identical to native musl wherever the C code is the same) and a 10,000-thread stress match native musl (LC-20 to LC-22). The ffmpeg portable build matches native on all 31 media fixtures and on all 1,150 frames of the private 4K HDR video. The SPEC audit (LC-23) passes 58 of 101 entries in portable mode, against 20 before; each of the other 43 has a cause. With `-funsigned-char` (BE-41) and per-benchmark Linux identity flags (LC-31), 67 pass as of 2026-10-31. Open: the performance guard (LC-24: stdio and printf with `%f` are 1.2x to 1.6x slower than the hand-written runtime, malloc is faster), the time-zone fallback for hosts without zoneinfo (LC-09), host services .NET cannot express (LC-30). The owner confirmed the ADR-09 tag and ADR-14 on 2026-10-31.
- **M4 portable C++ runtime.** libc++ and libc++abi 22.1.8 from the sysroot, with type_info, `dynamic_cast` and catch matching from libc++abi (CX-03) and a documented exception bridge (CX-02). libc++'s lit suite passes 5,102 of 5,172 tests in the CX-08 directories (98.6%); localization is 85.8% because musl has only C and C.UTF-8 locale data. libc++abi's own suite passes 52 of 59 (CX-13); the 7 failures are the unwinder interface the CLR replaces, libc++abi's exception globals and unwinding before `std::terminate`. Its first run found four bridge bugs, fixed. The allocation ABI (CX-09) matches native; SPEC C++ is classified (CX-11). Open: named locales beyond C and C.UTF-8 (CX-06), the libc++ suite under NativeAOT.
- **M5 Fortran runtime.** flang-rt serves both modes (FT-09), so the C# Fortran runtime is deleted. FCVS passes 172 of 172 and the gfortran run tests 2,682 of 2,687 (99.8%), after BE-39 and BE-40 made portable direct calls follow x86-64's register-slot rules. Every non-OpenMP SPEC Fortran rate entry passes except PALM (portable ABI) and cactuBSSN (glibc behavior); FT-07 is done. Open: portable OpenMP (FT-08), which 25 SPEC speed entries need.

## Progress (2026-10-30, M0 to M2)

Work on the `m0-m2` branch completed the M1 and M2 exit items and every M0 item except running the PR gate on GitHub. Measurements are in [evidence/2026-10-30-m0-m2.md](../evidence/2026-10-30-m0-m2.md).

- **M0 foundations.** License (MIT), notices, versioning, pinned sources, a parallel test runner (77 tests, about 3 minutes with eight jobs), 61 unit tests, JIT validation of every emitted method, and a CI workflow whose PR gate passes in the matching container. Not yet: a first run on GitHub (FD-19). Differential fuzzing (FD-16) runs locally and in the nightly job; automatic test-case reduction does not.
- **M1 backend hardening.** 44,762 IR semantics probes match native bit for bit; linkage, termination order, determinism, atomics, alignment, call-signature policy, vectorization and diagnostics are done; the support matrix is generated. Six behavior bugs and several missing features were found and fixed (listed in the evidence file). Fuzzing 1,000 csmith and 1,000 YARPGen programs found one bug, fixed (globals beyond 2 GB, BE-35), and one limitation (integers wider than 128 bits, BE-36); the two-week fuzzing criterion starts with the first nightly run on GitHub.
- **M2 library spike.** Unmodified musl 1.2.5, upstream libc++/libc++abi and flang-rt 22.1.8 compile completely for dotnet64-v1 and match native output under JIT and NativeAOT, through `--portable-libc` and a host layer of four entry points. ADR-01, 02, 03 and 07 are accepted. New decisions needed before M3: the ABI tag of the musl sysroot (LC-26, ADR-09) and the Linux identity of portable programs (LC-27, ADR-14).

## Status snapshot (2026-10-30, before M0)

Verified on this host during the plan review (Linux x86-64, .NET SDK 10.0.403, runtime 10.0.14, LLVM/Clang/Flang 22.1.8):

| Check | Result |
| --- | --- |
| `dotnet build llvmnet.slnx -c Release` | succeeded, 0 warnings |
| `tests/smoke.sh`, `tests/driver.sh` | pass (2 s and 8 s) |
| `tests/all.sh core` (53 scripts, JIT and NativeAOT, both ABIs) | pass, 151 checks, 376 s wall (sequential runner before FD-12) |
| Integration, ffmpeg, SPEC tags | not rerun today; last evidence 2026-09-08 in the status page of that date (since replaced by the evidence files) |

What exists and works, per the September evidence: C, C++ and Fortran compile through Clang/Flang 22 to CIL; three runtime modes (`managed-host`, `system`, `portable`); NativeAOT output; a relocatable toolchain package; the ffmpeg browser profile with threading and SIMD128 kernels matching native hashes on a 4K HDR file; every installed SPEC CPU 2017/2026 entry validated on test inputs in system mode, 20 of 101 in portable mode.

Size of the code base: 372 tracked files (2.1 MB); about 10,400 lines of C# (3,500 compiler, 6,900 runtime), 271 lines of C++ bridge, 798 lines of hand-written portable headers, 2,200 lines of Bash tests and 4,100 lines of test fixtures. The runtime exports 538 C-callable entry points (275 libc, 31 pthread, 60 C++ ABI, 126 Fortran, 46 SIMD128).

Prototype-grade areas, in order of impact:

1. **The portable C library** is hand-written C# with no conformance baseline, thread-safety and buffering defects, magic errno/flag constants, and large gaps (no `locale.h`, `wctype.h`, `fenv.h`, `complex.h`, `sys/mman.h`, sockets, `regex.h`, and more). Details in [02-portable-c-library.md](02-portable-c-library.md).
2. **The C++ and Fortran runtimes** are partial hand-written implementations of the libc++ ABI and the Flang runtime; iostreams, locale, final procedures, defined I/O and most reductions are missing. Details in [03-cxx-runtime.md](03-cxx-runtime.md) and [04-fortran-runtime.md](04-fortran-runtime.md).
3. **Engineering infrastructure**: no license, no CI, no unit tests, sequential Bash gates that stop at the first failure, machine-specific paths in integration scripts, hard-coded version string. Details in [00-foundations.md](00-foundations.md).
4. **The backend** is solid where tested but monolithic (one 1,500-line file), has a process-wide atomics lock, no debug information, no diagnostics with source locations, and no support matrix. Details in [01-compiler-backend.md](01-compiler-backend.md).
5. **Cross-platform execution** of portable images is designed but has never been exercised outside Linux x86-64. Details in [07-cross-platform.md](07-cross-platform.md).

## Goal and definition of production-ready

**Goal.** A compiler toolchain that turns C, C++ and Fortran sources into .NET assemblies which run unchanged on every supported .NET host (portable mode) or interoperate with the native Linux ABI (system mode), with behavior that matches native Clang/Flang builds or fails loudly at compile time.

**Production-ready (1.0) means all of the following are true and checked by CI:**

| # | Criterion | Measured by |
| --- | --- | --- |
| R1 | Correctness: a program accepted by llvmnet behaves as its native build does, or compilation fails with a diagnostic. No silent miscompiles, no stubbed-out operations. | WG-01/02 conformance suites at or above 99%, fuzzing (FD-16) with no open miscompile, library suites (WG-03) |
| R2 | Complete standard libraries: C (musl), C++ (libc++), Fortran (flang-rt) with their upstream test suites passing in portable mode, except a documented ENOSYS set. | LC-19, CX-08, FT-06 |
| R3 | Real workloads: ffmpeg FATE subset and SPEC CPU 2017/2026 test and train inputs pass in both modes under JIT and NativeAOT. | WG-04, WG-06 |
| R4 | Platforms: the same portable image runs on Linux x64/ARM64, Windows x64 and macOS ARM64, JIT and NativeAOT. | XP-01..03 in CI |
| R5 | Diagnosable: compiler errors carry file and line; runtime faults report the C call stack; `-g` yields source lines in stack traces. | BE-04, BE-28, BE-29 |
| R6 | Distributable: signed, reproducible, versioned packages with a license and third-party notices; documented driver, ABI and limitations. | TC-11, TC-16, FD-01/02/05 |
| R7 | Performance: published numbers against native on a fixed benchmark set, with regressions guarded; no numeric target for 1.0 (ADR-08). | PF-01, ADR-08 |
| R8 | Maintainable: PR gate under 30 minutes, unit tests, no file over about 600 lines in the compiler, support matrix generated from code, a contribution guide. | FD-12..15, BE-01/02 |

## Strategy

Three bets shape the sequencing:

1. **Replace, do not extend, the hand-written libraries.** Compile upstream musl, libc++/libc++abi and flang-rt to portable bitcode with llvmnet itself, leaving a small versioned host layer in C#. This converts an open-ended reimplementation into a porting task with upstream test suites as the finish line. It is the same architecture emscripten and wasi-libc use. [ADR-01](decisions.md#adr-01-portable-c-library-strategy), [ADR-02](decisions.md#adr-02-c-runtime-strategy), [ADR-03](decisions.md#adr-03-fortran-runtime-strategy).
2. **Make conformance suites the definition of done.** `llvm-test-suite`, GCC torture, musl `libc-test`, libc++ lit, FCVS and ffmpeg FATE replace "the workload we tried last passed".
3. **Build the infrastructure before the big rewrite.** License, CI, parallel test runner, unit tests and IL verification come first so the library replacement is reviewable in small steps.

Non-goals for 1.0: native C++ ABI interoperability, sockets in portable mode, system-ABI modes on non-Linux hosts, a sandbox or memory-safety guarantee for untrusted code.

## Milestones

| Milestone | Purpose | Depends on | Rough effort (engineer-weeks) |
| --- | --- | --- | --- |
| [M0](#m0-foundations) Foundations | License, CI, test runner, unit tests, reproducible inputs | | 3 to 4 |
| [M1](#m1-backend-hardening) Backend hardening | Semantics audits, diagnostics, atomics, linkage, limits | M0 | 4 to 6 |
| [M2](#m2-library-architecture-spike) Library architecture spike | Prove musl, libc++, flang-rt build and run; accept ADR-01..03, 07 | M1 (partly) | 4 to 6 |
| [M3](#m3-portable-c-library) Portable C library | musl + host layer; delete hand-written libc and `managed-host` | M2 | 6 to 10 |
| [M4](#m4-portable-c-runtime) Portable C++ runtime | libc++/libc++abi delivery | M3 | 4 to 6 |
| [M5](#m5-fortran-runtime) Fortran runtime | flang-rt delivery, portable OpenMP | M4 | 4 to 6 |
| [M6](#m6-cross-platform) Cross-platform | Windows/macOS/ARM64 JIT and NativeAOT in CI | M3 | 4 to 6 |
| [M7](#m7-toolchain-and-distribution) Toolchain and distribution | Driver table, packaging, docs, system-ABI hardening | M3 | 4 to 6 |
| [M8](#m8-performance) Performance | Benchmarks, atomics, allocas, method splitting, AOT policy | M3 | 4 to 8 |
| [M9](#m9-workload-gates-and-10) Workload gates and 1.0 | Full suites on the release candidate | all | 4 to 6 |
| [M10](#m10-native-extensions) Native extensions (before 1.0) | Proxied host libraries for portable programs: `dlopen` in the runtime, extension packages, the generator, the `gles` extension | M6, M7 | 6 to 10 |
| [M11](#m11-simd) SIMD (before 1.0) | Vector lowering to `Vector128`/`Vector256`, intrinsic helpers, native kernel packages, CPU detection, vectorizer policy | M6, M8 | 12 to 20 |
| [M12](#m12-cil-shaped-for-ryujit) CIL shaped for RyuJIT (proposed) | Tiering settings, switch lowering, constant global addresses, a per-method JIT budget, ReadyToRun for one runtime identifier | M8 | 3 to 5 |

The effort column is a rough order of magnitude for one experienced engineer and will be wrong in detail; it exists so milestones can be compared and so the total (roughly 45 to 65 engineer-weeks) is visible. M6, M7 and M8 can run in parallel with M4 and M5 once M3 is done.

### Status board

Update weekly. "Open P0" counts unchecked P0 items that the milestone's exit criteria name.

| Milestone | Target version | Status | Open P0 | Last updated |
| --- | --- | --- | ---: | --- |
| M0 | 0.3 | exit items done except the first GitHub run of the PR gate | 1 (FD-19) | 2026-10-30 |
| M1 | 0.3 | exit items done; two-week fuzzing criterion open (FD-16) | 0 | 2026-10-30 |
| M2 | 0.3 | done; ADR-01, 02, 03, 07 accepted | 0 | 2026-10-30 |
| M3 | 0.4 | done; musl is the portable C library, SPEC parity holds with the ADR-14 exclusions | 0 | 2026-10-31 |
| M4 | 0.5 | done; libc++ localization 100%, input.output 97.8% (LC-30 services), SPEC C++ parity holds | 0 | 2026-10-31 |
| M5 | 0.5 | done; flang-rt and portable OpenMP delivered (FT-08), 22 of 25 OpenMP speed entries pass, the rest excluded | 0 | 2026-10-31 |
| M6 | 0.6 | exit items done on all three hosts; two weeks of green CI runs need GitHub (FD-19) | 0 | 2026-10-31 |
| M7 | 0.7 | exit items done; signed packages from a tag need the repository on GitHub (FD-19) | 0 | 2026-10-31 |
| M8 | 0.7 | exit items done (PF-01, PF-02, benchmark table published); the first GitHub run of the nightly benchmark job needs FD-19. P1: PF-03, 04, 05, 08, 10, 11, 13, 14, 16 done, PF-06, PF-07 and PF-15 in progress | 0 | 2026-11-02 |
| M9 | 1.0-rc | in progress: WG-02, 03, 04, 05, 09 done, WG-01 at 99.1% portable and 98.8% system (target 99%), WG-06 not run (FD-21); RL-02, 05, 08, 09 done; two nightlies on a release candidate need GitHub (FD-19) | 2 (WG-01, WG-06) | 2026-11-01 |
| M10 | before 1.0 | exit items done (NX-02 to NX-06): images without extensions byte-identical, ABI freeze unchanged, `gles` native pixels on Linux x64, Windows x64 and macOS ARM64; the CI jobs need FD-19. P2: NX-07 to NX-13 done; NX-14 deferred by the owner | 0 | 2026-11-03 |
| M11 | before 1.0 | in progress: SV-01, SV-02 done (vector IR runs as `Vector128` code; benchmark set unchanged); SV-03 started | 0 | 2026-11-03 |
| M12 | proposed | proposed 2026-11-04: RyuJIT review done, measured on kernels, ffmpeg and 22 benchmarks; ADR-17 and the placement are the owner's | 0 | 2026-11-04 |

### M0: Foundations

Exit: FD-01, FD-02, FD-05, FD-06, FD-09, FD-12, FD-13, FD-14, FD-15, FD-19 done; PR gate green on three consecutive merges. Details: [00-foundations.md](00-foundations.md). Decisions: ADR-13 (license).

### M1: Backend hardening

Exit: BE-04, BE-05, BE-07, BE-08, BE-11, BE-12, BE-15, BE-16, BE-18, BE-25, BE-31 done; two weeks of fuzzing without an open miscompile; support matrix published. Details: [01-compiler-backend.md](01-compiler-backend.md). Decisions: ADR-06.

### M2: Library architecture spike

Exit: LC-01, LC-02, CX-01, FT-01 executed with written results; LC-03 recorded; ADR-01, ADR-02, ADR-03 and ADR-07 Accepted or replaced; every backend blocker found has a BE item. This milestone exists to fail early if the strategy is wrong. Details: [02-portable-c-library.md](02-portable-c-library.md), [03-cxx-runtime.md](03-cxx-runtime.md), [04-fortran-runtime.md](04-fortran-runtime.md).

### M3: Portable C library

Exit: LC-04 through LC-10, LC-14 through LC-17, LC-19 through LC-23 and LC-26 done; portable ffmpeg profile, 31 fixtures and the 4K HDR gate pass; SPEC portable C entries at parity with system mode as ADR-14 defines it; `managed-host` removed. Details: [02-portable-c-library.md](02-portable-c-library.md). Decisions: ADR-04.

### M4: Portable C++ runtime

Exit: CX-02 through CX-08 done; libc++ lit directories at or above 98%; SPEC C++ portable parity. Details: [03-cxx-runtime.md](03-cxx-runtime.md).

### M5: Fortran runtime

Exit: FT-02 through FT-07 done; FCVS and `llvm-test-suite/Fortran` at or above 98%; SPEC Fortran portable parity. Details: [04-fortran-runtime.md](04-fortran-runtime.md).

### M6: Cross-platform

Exit: XP-01, XP-02, XP-03, XP-06 done and green for two consecutive weeks. Details: [07-cross-platform.md](07-cross-platform.md). Decisions: ADR-05.

### M7: Toolchain and distribution

Exit: TC-01 through TC-05, TC-11 through TC-13, TC-16, TC-17 done; SA-01 done (`--native-import` removed); signed packages produced by CI from a tag. Details: [06-driver-and-toolchain.md](06-driver-and-toolchain.md), [05-system-abi.md](05-system-abi.md). Decisions: ADR-09, ADR-11, ADR-12.

### M8: Performance

Exit: PF-01 and PF-02 done; nightly benchmark table published; no numeric target for 1.0 (ADR-08). Details: [08-performance.md](08-performance.md).

### M9: Workload gates and 1.0

Exit: WG-01 through WG-06 at threshold on the release candidate for two consecutive nightlies; every RL item ticked; the M10 and M11 exit criteria met (owner, 2026-11-03). Details: [09-workload-gates.md](09-workload-gates.md), [10-release.md](10-release.md).

### M10: Native extensions

Exit: NX-02 through NX-06 done; a portable program using the `gles` extension renders the native pixels on Linux x64, Windows x64 and macOS ARM64 under the JIT and as NativeAOT executables in CI; programs without extensions are byte-identical to before; the ABI freeze test passes unchanged. Accepted, and required before 1.0 by the owner's decision of 2026-11-03 ([ADR-15](decisions.md#adr-15-native-extensions-of-the-portable-abi)). Details: [11-native-extensions.md](11-native-extensions.md).

### M11: SIMD

Exit: SV-02 through SV-05, SV-07, SV-08, SV-12, SV-13, SV-15, SV-19 and SV-20 done; vectorized and intrinsic-using code from the survey corpus runs as vector code on all four hosts with output identical to native; the ffmpeg browser profile runs its nasm kernels from a kernel package on Linux x64 (JIT and NativeAOT) and its C paths elsewhere, with native hashes; the benchmark table shows no regression at any level; a program whose IR has no vector operations is byte-identical to before, and no program of the benchmark set is slower (reworded by the owner on 2026-11-03, [decisions](decisions.md#decided-by-the-owner-2026-11-03-sv-02)); the ABI freeze test passes unchanged. Accepted, and required before 1.0 by the owner's decision of 2026-11-03 ([ADR-16](decisions.md#adr-16-simd-levels)). Details: [12-simd.md](12-simd.md).

### M12: CIL shaped for RyuJIT

Exit: JP-02 to JP-07 and JP-09 done. The benchmark set's JIT geometric mean is lower than before the milestone, and no benchmark is slower beyond the nightly's thresholds, under the JIT or NativeAOT. The portable image stays one platform-neutral file, and the ABI freeze test passes unchanged. Proposed 2026-11-04; placement before or after 1.0 and [ADR-17](decisions.md#adr-17-readytorun-for-jit-deployments) are the owner's. Details: [13-jit-patterns.md](13-jit-patterns.md).

## Workstreams

| Prefix | File | Scope |
| --- | --- | --- |
| FD | [00-foundations.md](00-foundations.md) | License, build, test runner, unit tests, CI |
| BE | [01-compiler-backend.md](01-compiler-backend.md) | IR-to-CIL correctness, structure, diagnostics, limits, debug info |
| LC | [02-portable-c-library.md](02-portable-c-library.md) | musl port, host layer, libc conformance |
| CX | [03-cxx-runtime.md](03-cxx-runtime.md) | libc++/libc++abi, exception bridge, RTTI |
| FT | [04-fortran-runtime.md](04-fortran-runtime.md) | flang-rt, Fortran conformance, portable OpenMP |
| SA | [05-system-abi.md](05-system-abi.md) | Native bridge hardening, callbacks, TLS, signals |
| TC | [06-driver-and-toolchain.md](06-driver-and-toolchain.md) | Driver options, outputs, packaging, documentation |
| XP | [07-cross-platform.md](07-cross-platform.md) | Windows, macOS, ARM64, NativeAOT matrix |
| PF | [08-performance.md](08-performance.md) | Benchmarks and known hot spots |
| WG | [09-workload-gates.md](09-workload-gates.md) | Conformance suites, ffmpeg FATE, SPEC |
| RL | [10-release.md](10-release.md) | Pre-release train and the 1.0 checklist |
| NX | [11-native-extensions.md](11-native-extensions.md) | Proxied host libraries (EGL/GLES, Vulkan) for portable programs; ADR-15 accepted, M10 before 1.0 |
| SV | [12-simd.md](12-simd.md) | SIMD levels: vector lowering, intrinsic helpers, native kernel packages, CPU detection; ADR-16 accepted, M11 before 1.0 |
| JP | [13-jit-patterns.md](13-jit-patterns.md) | CIL and runtime settings shaped for RyuJIT's limits and patterns, ReadyToRun for JIT deployments; proposed 2026-11-04 (M12, ADR-17) |

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| The musl thread/TLS layer cannot be mapped onto managed threads cleanly | M3 slips; pthreads stay hand-written | LC-02 is a spike with a named fallback (keep C# pthreads, exclude musl `src/thread`); picolibc is the libc fallback |
| Very large functions (SPEC gcc, perlbench, wrf) hit JIT MinOpts or CLR local limits | Performance and, at the extreme, compile failures | BE-25 measures first; BE-26 splitting; documented limits in the meantime |
| Debug information turns out to be large | R5 partially missed | ADR-12 scopes 1.0 to sequence points only |
| Cross-platform NativeAOT CI is expensive and flaky | M6 slips | Start with JIT on all platforms (XP-01/02), AOT nightly only |
| LLVM major version churn (new release every six months) | Upgrade cost every cycle | ADR-06 pins one major per release; the IR corpus is the regression net |
| Licensed SPEC kits cannot run in public CI | Release gate depends on a private runner | FD-21 documents the runner; WG-06 publishes summaries only |
| Single maintainer | Throughput and bus factor | M0 makes the project contributable; the plan itself is the hand-over document |
| Performance gap versus native discourages adoption | R7 missed | ADR-08 defers a numeric target until the correctness gates pass; PF-01 publishes numbers early rather than late |

## Suggested first four weeks

1. Accept or amend ADR-13 (license) and ADR-01/02/03/07 as working hypotheses so the spike can start.
2. FD-01, FD-02, FD-05 (one day each), then FD-12/FD-13 (runner, build once) and FD-19 (PR gate).
3. In parallel, BE-07 (linkage), BE-16 (atomics) and BE-04 (diagnostics), because the musl spike will hit all three.
4. Start LC-01 as soon as BE-07 lands; report blockers as BE items weekly.
