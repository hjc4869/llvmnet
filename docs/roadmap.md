# Roadmap

The ordering follows feature correctness first, ffmpeg before SPEC, and performance last.

1. Harden C semantics: remaining memory/calling-convention corners, TLS globals, dynamic stack lifetime, returns-twice/nonlocal jumps, computed gotos, full scalar intrinsic coverage, and C runtime conformance tests.
2. Complete the ffmpeg gate: upstream FATE coverage, fixtures for remaining enabled decoders and demuxers, error/truncation paths, repeatable clean builds, and reusable managed-library packaging. Preserve the existing 4K HDR full-file comparison.
3. Expand integer-rate one-copy coverage across both SPEC suites. Selected C/C++/Fortran development runs and ordinary NativeAOT harness configurations now work, but are starting points, not suite completion. Complete system/flag disclosure and reportability review without checking in licensed materials.
4. Expand C++ runtime/library coverage and Flang's descriptor, character, I/O, complex, and derived-type support as actual workloads require. Prefer compiling appropriate upstream runtime code when the backend and platform boundary can support it.
5. Expand FP-rate coverage, then integer/FP speed with correct OpenMP behavior. Single-thread suppression is useful for initial diagnosis, but must not be presented as full parallel or reportable speed support.
6. After correctness coverage is complete, compare against native clang/flang 22 and `/usr/bin/emcc` with wasmtime. Wasmtime has not been installed by this bring-up. Keep inputs, source revisions, scalar/assembly settings, thread counts, optimization levels, warmup, output validation, and timing boundaries explicit.
7. Investigate portable vector acceleration after a scalar vector-semantics implementation exists. A logical wasm-style 128-bit vector must not assume `Vector<T>.Count` is fixed at 128 bits. Handle lane widths, masks, narrowing, saturation, signedness, and floating-point semantics first; avoid architecture-specific intrinsic dependencies.
8. Harden system-ABI native C interoperability separately from managed linking. Generated call bridges, scalar callback thunks, data imports, and mode validation are implemented. Expand callback/library lifetime and additional host ABIs; native C++ ABI compatibility is not a priority.
9. Validate the same dotnet64-v1 CIL images on Windows/macOS/Linux x64/ARM64. Extend the bundled portable libc/libc++/Fortran runtime without inheriting host structures. Version any incompatible ABI change and retain explicit mixed-mode rejection. Complete compiler distribution signing/manifests, upstream source provenance, and offline NativeAOT SDK provisioning before calling the distribution production-ready.

## Reproducible Evidence

- Core correctness: `bash tests/all.sh core`.
- Source-backed integrations: `bash tests/all.sh integration`.
- Full video and selected SPEC test inputs: `bash tests/all.sh spec-test`.
- Preliminary full-video timing: `bash scripts/benchmark-video.sh`.
- Emitted image inspection: `dotnet tests/LlvmNet.Checks/bin/Release/net10.0/LlvmNet.Checks.dll inspect artifacts/ffmpeg-browser/decode.dll`.

Performance work should preserve the native-vs-CIL correctness checks. Do not replace an unsupported operation with an apparently successful stub merely to advance a benchmark.