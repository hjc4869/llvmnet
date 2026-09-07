# Verified Status

Initial bring-up on 2026-09-07: Linux x86-64, LLVM/Clang/Flang 22.1.8, .NET SDK 10.0.400, .NET runtime 10.0.11. This document records development evidence, not a claim of full language, emcc, or SPEC conformance.

## Deployable Toolchain Update

Implemented installed `llvmnet-clang`, `llvmnet-clang++`, `llvmnet-flang`, archiver/indexer/symbol utilities, response files, executable launchers, NativeAOT, a bundled versioned portable C/libc++ sysroot, and explicit system-library mode. The approximately 638 MiB uncompressed fully bundled Linux installation compiles portable C/C++/Fortran with no `dotnet` or LLVM commands on PATH. NativeAOT publishing still needs the .NET SDK and target native link tools.

System-mode tests exercise real native libc/stdio, native varargs, struct return/byval/sret, data exports, native qsort/pthread callbacks, and NativeAOT TLS teardown. Portable tests exercise fixed data layouts, allocation, C++ standard exceptions/containers/algorithms, managed threads, deterministic errno strings, and C/Fortran extended-precision interoperability. Portable outputs have no generated native ABI imports. Windows/macOS/ARM64 execution remains unverified.

Normal CPU 2017 `runcpu` configurations have successfully built, staged, run, and validated mcf, deepsjeng, and exchange2 test inputs in both system and portable NativeAOT modes. The system-mode mcf single-copy reference-rate workload also passed. CPU 2026 zstd and lbm test inputs passed the same ordinary system-mode NativeAOT harness workflow. These normal harness checks are distinct from the earlier manual development runners below; they are not complete reportable suite results. Details and exact commands are in [toolchain.md](toolchain.md).

The final dual-ABI toolchain gate passed, followed by all 31 existing ffmpeg media fixtures, DASH/HLS, scaling/resampling/library checks, and a ten-frame check on the supplied HDR video. Compiler/runtime/native-bridge editor diagnostics were clear. Windows/macOS/ARM64 portable execution and full reportable SPEC suites remain open release gates.

## ffmpeg

The component list from the supplied browser build is reproduced in [../config/ffmpeg-browser.sh](../config/ffmpeg-browser.sh). The full build enables pthreads in system, portable, and legacy managed-host modes. The optional `FFMPEG_SIMD128=1` profile selects upstream wasm HEVC C SIMD kernels backed by .NET `Vector128<T>` in both ABIs. Native assembly, hardware decoding, native CPU dispatch, and networking remain disabled. The build produces LLVM bitcode libraries and a CIL decode harness, whose default remains one thread. It does not yet package a drop-in managed ffmpeg command-line application.

Verified:

- dav1d 1.5.4: generic C build, initialization/cleanup, and real AV1 decoding.
- libxml2: single-threaded build, XML parsing, and DASH manifests.
- zlib 1.3.2: compression/decompression round-trip and PNG decoding.
- Seven initial fixtures: H.264, VP9, AV1, PNG, PCM, FLAC, AAC.
- Twenty-four additional fixtures: MPEG-1/2/4, MSMPEG4 v2/v3, WMV1/2, VP8, Theora, ProRes, MJPEG, TIFF, WebP, ALAC, AC-3/E-AC-3, MP2/MP3, Opus, Vorbis, float PCM, 24-bit PCM, A-law and mu-law.
- Local-file DASH and HLS playback with no networking.
- swscale and swresample output hashes; avfilter graph allocation and avdevice registration with filters/devices disabled as requested.
- All 1,150 frames of `/home/david/Videos/20260521_215039.mp4`: 3840x2160 HEVC Main 10, yuv420p10le, BT.2020/HLG, about 120 Mb/s. Native/CIL active-pixel hashes matched exactly.

Not every enabled legacy decoder has a fixture, and the full upstream FATE suite has not been run. libjxl is not validated.

### Threading Update

System and portable dependencies and ffmpeg now build in separate ABI-tagged directories. Portable dependency bring-up added missing public errno, descriptor-control, directory, and math declarations. System libxml2 required checked internal variadic callback dispatch; actual native invocation of a variadic callback still fails explicitly. Upstream sources remain unchanged.

The [threaded media regression](../tests/ffmpeg-threads.sh) passed 96 managed cases: H.264 and 10-bit HEVC frame/slice modes, VP9 frame mode, and dav1d AV1 workers, at two/four threads, in both ABIs under JIT and NativeAOT. Each compares exact frame hashes with single-thread native output and also checks threaded native output. Full 24-frame draining and three-frame early teardown passed; ffmpeg-managed frame/slice activation is asserted. The portable decoder contains 7,202 CIL method bodies and zero generated P/Invoke imports.

Four-thread decoding of the first ten frames of the supplied 4K HDR video matched single-thread native hashes in both ABIs under JIT and NativeAOT. All 31 existing media fixtures, local DASH/HLS, and scaling/resampling/library checks also passed on the newly threaded legacy profile. These runs do not constitute full threaded FATE coverage, cross-platform validation, or a threading performance result. libxml2 remains configured without threading. Build commands and scope are in [ffmpeg-threading.md](ffmpeg-threading.md).

### SIMD128 Update

The opt-in `--simd128` compiler/header path preserves SIMD helper values and vector memory operations using architecture-neutral `Vector128<T>`. The same 21-operation, 67-record C probe now matches real WebAssembly in emitted system/portable CIL under JIT, forced software fallback, baseline NativeAOT, and host-targeted NativeAOT. Upstream HEVC DSP dispatch is asserted; 512 IDCT and 144 SAO cases match scalar output, and real 8/10-bit frame/slice-threaded fixtures pass in both ABIs. The SIMD portable decoder has 7,216 IL method bodies and zero generated P/Invoke imports.

All 2,829 decoded frames across the six supplied `/home/david/Videos/2026*.mp4` files match installed native ffmpeg hashes in both SIMD JIT ABIs. Three-run decode-only measurements show aggregate JIT throughput gains of 12.7% system / 12.9% portable; portable SIMD still takes 2.49x installed native elapsed time. Explicit host-targeted AOT gains about 15%, while baseline SIMD AOT regresses by about 2.2x versus scalar AOT. The baseline remains default; `--aot-instruction-set native` is an explicit, host-CPU-specific deployment choice. Full tables, fairness caveats, and commands are in [video-simd128-performance.md](video-simd128-performance.md).

This is HEVC IDCT at 8/10 bits and SAO at 8 bits, not complete SIMD128, general auto-vectorized hardware lowering, or wasm32 module support. The supported source-header subset and remaining work are in [simd128.md](simd128.md).

### Preliminary Video Measurement

The earlier single-threaded complete-file validation also measured total wall time, including startup, JIT, file I/O and hashing:

| Execution | Seconds | Maximum RSS KiB |
| --- | ---: | ---: |
| Native clang from the same linked bitcode | 190.53 | 251472 |
| .NET CIL | 250.96 | 330528 |

This is approximately 1.32x native elapsed time, not an optimized decoder benchmark. No native assembly acceleration is enabled in this comparison. There is no emcc/wasmtime comparison yet. Latest rerun measurements live under `artifacts/ffmpeg-browser/video-validation/` and may differ.

The complete `tests/all.sh spec-test` rerun also passed: 186.84 seconds native and 253.26 seconds CIL, about 1.36x, with all 1,150 frame hashes equal. A separate fresh-configure build passed the HDR check and contained 7,137 CIL method bodies with zero P/Invoke imports. Its count differs slightly from the earlier cached configuration because additional frontend feature probes now succeed.

## SPEC Development Runs

The optimized benchmark-by-benchmark inventory and remaining failures are recorded in [spec-coverage.md](spec-coverage.md). It is distinct from the earlier selected manual runs below; attempted coverage is not full-suite success.

These use one benchmark process, source/input archives from the supplied licensed media, and no reportable SPEC harness configuration. Original kits remain unchanged. No proprietary benchmark source or input is part of the tracked project.

| Suite | Benchmark | Language | Validated Inputs |
| --- | --- | --- | --- |
| CPU 2017 | 505.mcf_r | C | test, train, refrate |
| CPU 2017 | 531.deepsjeng_r | C++ | test, train |
| CPU 2017 | 548.exchange2_r | Fortran | test, train |
| CPU 2017 | 519.lbm_r | C, FP rate | test |
| CPU 2026 | 777.zstd_r | C | test self-check workload |
| CPU 2026 | 782.lbm_r | C, FP rate | test |

mcf, deepsjeng, and exchange2 matched native and the supplied output artifacts. lbm matched native output exactly, with the native output checked against the benchmark's declared `1e-7` absolute tolerance. zstd completed its built-in compression/decompression verification; its timing text is not treated as deterministic reference output.

The complete integer-rate suites, complete FP-rate suites, and integer/FP speed suites have not been validated. No SPEC performance scores are reported.

## Compiler Regression Coverage

The core runner covers scalar control flow, parallel PHIs, LLVM 22 switches, memory/global relocations, function pointers, large `byval`/`sret` structures, mixed SysV varargs, libc adapters, C11 atomics, pthread contention, C++ templates/RAII/RTTI/unwinding, Fortran descriptors/allocation/I/O, x87 extended precision, and 128-bit integer arithmetic. Tests generally compare native and CIL behavior, with direct IR cases where frontend optimization could remove the feature being tested.

Optimized regressions additionally compare `-O3 -fvectorize -fslp-vectorize` C with native output in system and portable JIT, NativeAOT, and managed-library configurations. Direct IR tests cover vector arithmetic, reductions, masks, constants, lane operations, function boundaries, and scalar `llvm.frexp` with i32 exponent results. Fortran checks now include O3, scalar CHARACTER INDEX/REPEAT/ADJUSTL, pointer allocation/bounds/alias deallocation, and formatted BACKSPACE with beginning/end-of-file handling in both ABIs and both execution modes. Explicit managed library exports take precedence over native symbol lookup in system mode.

Additional workload-driven checks cover legacy no-prototype calls, discarded return values and unused trailing arguments, scalar hyperbolic and integer-power intrinsics, 80/128-bit population/leading/trailing-zero counts, Fortran contiguity, temporary copy-in/copy-out and SPREAD, and portable basename/dirname, utime and strtok/strtok_r. Unimplemented pointer-derived-type lifecycle and unsupported filesystem operations still fail explicitly.

## Known Limits

- Not a complete compiler-driver replacement: unsupported native-linker flags and unresolved symbols are errors.
- No general `setjmp`/`longjmp`, LLVM dynamic stack save/restore, or computed-goto lowering yet.
- No LLVM TLS global lowering yet, despite managed pthread key support.
- General fixed-vector operations supported by LLVM reduction expansion/scalarization retain a scalar fallback. The opt-in SIMD128 source-header/helper path accelerates the tested HEVC subset, not arbitrary vector IR. Scalable vectors, direct target-specific vector intrinsics, complete masked-memory handling, binary128, and several wide-integer intrinsic families remain unsupported. Auto-vectorization is disabled by default. Baseline NativeAOT SIMD performance and cross-platform execution remain open.
- The C runtime is a tested subset, not complete glibc/POSIX. Locale, wide-character I/O, hexadecimal/long-double printf/scanf, full signal/process behavior, and some filesystem metadata are incomplete.
- C++ standard-library exports, exception pointer conversions, access/ambiguity corner cases, thread-local destruction, and complete library ABI coverage remain incomplete.
- Fortran derived types/finalization, complex/quad runtime operations, all array transformations, nondefault floating environments, unformatted/direct I/O, and full formatted/list-directed editing remain incomplete.
- OpenMP, MPI, and coarrays are not implemented. Speed/parallel benchmark coverage is therefore not claimed.
- The explicit scalar P/Invoke option remains limited. System mode adds tested C aggregate/varargs/data/callback bridges, but native C++ linkage, arbitrary callback signatures, native TLS globals and full native-runtime lifecycle remain incomplete.

Unsupported cases should remain visible diagnostics or explicit runtime errors, never silent native fallbacks or claims of successful emulation.