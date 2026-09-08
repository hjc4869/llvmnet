# Roadmap

The ordering follows feature correctness first, ffmpeg before SPEC, and performance last.

1. Harden C semantics: remaining memory/calling-convention corners, external native TLS and foreign-thread cleanup, cross-native nonlocal jumps, computed gotos, full scalar intrinsic coverage, and C runtime conformance tests. Defined TLS globals, scoped stack restoration, and translated returns-twice/nonlocal jumps have focused JIT/NativeAOT tests.
2. Complete the ffmpeg gate: upstream FATE coverage, fixtures for remaining enabled decoders and demuxers, error/truncation paths, repeatable clean builds, and reusable managed-library packaging. Frame/slice and dav1d threading now pass focused system/portable JIT/NativeAOT checks; broaden concurrent-context and long-running coverage. Preserve the existing 4K HDR full-file comparison.
3. Expand one-copy coverage across both SPEC suites. The per-benchmark matrix checks every installed rate/speed entry in system and portable ABI modes at O3, with explicit opt-in vectorization and retained failure evidence. Resolve each unsupported build/runtime operation and validate train/reference inputs; an attempted matrix is not suite completion. Complete system/flag disclosure and reportability review without checking in licensed materials.
4. Expand C++ runtime/library coverage and Flang's descriptor, character, I/O, complex, and derived-type support as actual workloads require. Prefer compiling appropriate upstream runtime code when the backend and platform boundary can support it.
5. Extend beyond the completed ordered system-ABI test-input validation of all CPU 2017/2026 rate and speed groups: clean final-toolchain reruns, train/reference inputs and portable blockers remain. Speed uses explicit OpenMP/host DO CONCURRENT lowering where applicable, while kit-defined serial and multiprocess behavior remains disclosed. Do not present single-thread suppression as parallel validation; portable OpenMP remains open.
6. After correctness coverage is complete, compare against native clang/flang 22 and `/usr/bin/emcc` with wasmtime. Wasmtime has not been installed by this bring-up. Keep inputs, source revisions, scalar/assembly settings, thread counts, optimization levels, warmup, output validation, and timing boundaries explicit.
7. Expand beyond the implemented [SIMD128 source-header/helper path](simd128.md), now used by ffmpeg HEVC IDCT and 8-bit SAO. Direct acceleration of generic vector IR, broader intrinsic/codec coverage, baseline NativeAOT code quality, managed vector library boundaries, and ARM64 execution remain open. The WebAssembly oracle, scalar kernel comparisons, and full-file hashes must remain gates. Do not assume `Vector<T>.Count` is fixed at 128 bits; avoid architecture-specific intrinsic dependencies. [Measured JIT/host-AOT gains and baseline-AOT regression](video-simd128-performance.md) must stay distinct.
8. Harden system-ABI native C interoperability separately from managed linking. Generated call bridges, scalar callback thunks, data imports, and mode validation are implemented. Expand callback/library lifetime and additional host ABIs; native C++ ABI compatibility is not a priority.
9. Validate the same dotnet64-v1 CIL images on Windows/macOS/Linux x64/ARM64. Extend the bundled portable libc/libc++/Fortran runtime without inheriting host structures. Version any incompatible ABI change and retain explicit mixed-mode rejection. Complete compiler distribution signing/manifests, upstream source provenance, and offline NativeAOT SDK provisioning before calling the distribution production-ready.

## Reproducible Evidence

- Per-benchmark optimized SPEC results and blockers: [spec-coverage.md](spec-coverage.md).
- Core correctness: `bash tests/all.sh core`.
- Source-backed integrations: `bash tests/all.sh integration`.
- Threaded ffmpeg in both ABIs, including NativeAOT: `FFMPEG_THREADS_AOT=1 bash tests/ffmpeg-threads.sh`.
- Explicit system LLVM OpenMP: `bash scripts/install-system-openmp.sh && bash tests/openmp.sh`.
- Architecture-neutral SIMD128 feasibility: `bash tests/wasm-simd128.sh`.
- Full video and selected SPEC test inputs: `bash tests/all.sh spec-test`.
- Every installed SPEC entry, both ABIs: `bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain test` and the corresponding `spec2026-harness` command. Build the toolchain with `bash scripts/package-toolchain.sh artifacts/spec-toolchain` first. Nonzero results preserve per-case diagnostics and must be resolved, not counted as passes.
- Preliminary full-video timing: `bash scripts/benchmark-video.sh`.
- Emitted image inspection: `dotnet tests/LlvmNet.Checks/bin/Release/net10.0/LlvmNet.Checks.dll inspect artifacts/ffmpeg-browser/decode.dll`.

Performance work should preserve the native-vs-CIL correctness checks. Do not replace an unsupported operation with an apparently successful stub merely to advance a benchmark.