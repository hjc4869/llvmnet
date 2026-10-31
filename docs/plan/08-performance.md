# Workstream PF: Performance

Prefix `PF`. Owner: unassigned. Milestone: [M8](README.md#m8-performance). Correctness gates always take precedence; no item here may replace an unsupported operation with a stub.

## Current state (verified 2026-10-30, from `docs/dev/video-benchmark.md` and `docs/user/limitations.md`)

- Four-thread 4K HEVC decoding under JIT: portable SIMD128 profile at 2.49x the elapsed time of the installed native ffmpeg (which uses hand-written assembly); scalar portable at about 2.8x. An earlier single-thread full-file run measured 1.32 to 1.36x native built from the same bitcode.
- NativeAOT with the baseline instruction set regresses the SIMD profile 2.2x against scalar AOT; host-targeted AOT gains about 15%. The cause has not been established.
- Known structural costs: the global atomics lock, heap-backed scoped allocas, unbuffered `printf`, per-operation `ConcurrentDictionary` lookups in pthread mutex/condition operations, software varargs records, exception-based `setjmp`, and JIT MinOpts fallback for very large methods.

## Items

- [ ] **PF-01** (M, P0) Benchmark harness and baselines: nightly runs of ffmpeg `bench` mode on redistributable clips, free workloads from `llvm-test-suite` (and the SPEC-like zstd/x264 sources where licenses allow), and microbenchmarks (`malloc`, `fwrite`, atomics, `calli`, varargs, `Float80`, `memcpy`), with results tables in `docs/evidence/` and regression thresholds that fail the nightly.
- [ ] **PF-02** (M, P0) Atomics ([BE-16](01-compiler-backend.md)) and musl futex-based mutexes measured on the threaded ffmpeg case; expected to remove the serialization visible at four threads. — **In progress** (`d280b0d`): Lock-free atomics measured on a contention fixture; musl futex mutexes measured only functionally.
- [ ] **PF-03** (M, P1) stdio: buffered musl I/O replaces per-call host writes; measure host-call cost and choose buffer defaults.
- [ ] **PF-04** (M, P1) Alloca arena ([BE-17](01-compiler-backend.md)) measured on recursion-heavy code (deepsjeng, perlbench).
- [ ] **PF-05** (M, P1) Indirect calls: measure `ldftn`/`calli` against delegate and function-pointer-table alternatives on x264 and ffmpeg DSP dispatch.
- [ ] **PF-06** (L, P1) JIT code quality: inspect hot kernels with `DOTNET_JitDisasm`, remove avoidable `UInt128` and normalization masks, tune `TieredPGO`/`TieredCompilation` settings in the emitted `runtimeconfig.json`, and evaluate ReadyToRun for startup ([TC-08](06-driver-and-toolchain.md)).
- [ ] **PF-07** (L, P1) Over-limit methods ([BE-25](01-compiler-backend.md), [BE-26](01-compiler-backend.md)): quantify the MinOpts penalty on SPEC gcc and perlbench, then outline.
- [ ] **PF-08** (M, P1) NativeAOT instruction sets: explain the baseline SIMD regression (hypotheses: non-constant shuffle indices or widening operations without hardware support under the baseline ISA), add `x86-64-v3` as an explicit middle option, document the trade-offs; keep baseline as the default. — **Decided** (2026-10-31): baseline stays the default; `x86-64-v3` is an explicit opt-in.
- [ ] **PF-09** (L, P2) General vector lowering ([BE-32](01-compiler-backend.md)). Post-1.0.
- [ ] **PF-10** (S, P1) Startup latency of `dotnet program.dll` hello-world and of NativeAOT binaries; trim the runtime assembly; `InvariantGlobalization` where safe.
- [ ] **PF-11** (M, P1) Memory: RSS versus native (330 MB versus 251 MB on the video test), GC configuration for non-GC workloads, large-page arenas for musl `malloc`.
- [ ] **PF-13** (S, P1) Every function-pointer materialization calls `SystemAbi.RegisterCallback`, a concurrent dictionary insert, even in portable mode where the identity is the method pointer itself. Register once per function, or not at all in portable mode.
- [ ] **PF-12** (S, P1) Agree the 1.0 performance target ([ADR-08](decisions.md#adr-08-performance-target)): proposed portable NativeAOT at or below 2.0x and JIT at or below 2.5x of native geomean on the free benchmark set, measured by PF-01. — descoped: ADR-08 (2026-10-31, no numeric target for 1.0; PF-01 still publishes numbers).
