# Verified Status

Initial bring-up on 2026-09-07: Linux x86-64, LLVM/Clang/Flang 22.1.8, .NET SDK 10.0.400, .NET runtime 10.0.11. This document records development evidence, not a claim of full language, emcc, or SPEC conformance.

## Deployable Toolchain Update

Implemented installed `llvmnet-clang`, `llvmnet-clang++`, `llvmnet-flang`, archiver/indexer/symbol utilities, response files, executable launchers, NativeAOT, a bundled versioned portable C/libc++ sysroot, and explicit system-library mode. The approximately 638 MiB uncompressed fully bundled Linux installation compiles portable C/C++/Fortran with no `dotnet` or LLVM commands on PATH. NativeAOT publishing still needs the .NET SDK and target native link tools.

System-mode tests exercise real native libc/stdio, native varargs, struct return/byval/sret, data exports, native qsort/pthread callbacks, and NativeAOT TLS teardown. Portable tests exercise fixed data layouts, allocation, C++ standard exceptions/containers/algorithms, managed threads, deterministic errno strings, and C/Fortran extended-precision interoperability. Portable outputs have no generated native ABI imports. Windows/macOS/ARM64 execution remains unverified.

Normal CPU 2017 `runcpu` configurations have successfully built, staged, run, and validated mcf, deepsjeng, and exchange2 test inputs in both system and portable NativeAOT modes. The system-mode mcf single-copy reference-rate workload also passed. CPU 2026 zstd and lbm test inputs passed the same ordinary system-mode NativeAOT harness workflow. These normal harness checks are distinct from the earlier manual development runners below; they are not complete reportable suite results. Details and exact commands are in [toolchain.md](toolchain.md).

The final dual-ABI toolchain gate passed, followed by all 31 existing ffmpeg media fixtures, DASH/HLS, scaling/resampling/library checks, and a ten-frame check on the supplied HDR video. Compiler/runtime/native-bridge editor diagnostics were clear. Windows/macOS/ARM64 portable execution and full reportable SPEC suites remain open release gates.

## ffmpeg

The single-threaded component list from the supplied browser build is reproduced in [../config/ffmpeg-browser.sh](../config/ffmpeg-browser.sh). Assembly, hardware acceleration, runtime CPU dispatch, networking, and ffmpeg threading are disabled. The build produces LLVM bitcode libraries and a CIL decode harness. It does not yet package a drop-in managed ffmpeg command-line application.

Verified:

- dav1d 1.5.4: generic C build, initialization/cleanup, and real AV1 decoding.
- libxml2: single-threaded build, XML parsing, and DASH manifests.
- zlib 1.3.2: compression/decompression round-trip and PNG decoding.
- Seven initial fixtures: H.264, VP9, AV1, PNG, PCM, FLAC, AAC.
- Twenty-four additional fixtures: MPEG-1/2/4, MSMPEG4 v2/v3, WMV1/2, VP8, Theora, ProRes, MJPEG, TIFF, WebP, ALAC, AC-3/E-AC-3, MP2/MP3, Opus, Vorbis, float PCM, 24-bit PCM, A-law and mu-law.
- Local-file DASH and HLS playback with no networking.
- swscale and swresample output hashes; avfilter graph allocation and avdevice registration with filters/devices disabled as requested.
- All 1,150 frames of `/home/david/Videos/20260521_215039.mp4`: 3840x2160 HEVC Main 10, yuv420p10le, BT.2020/HLG, about 120 Mb/s. Native/CIL active-pixel hashes matched exactly.

Not every enabled legacy decoder has a fixture, and the full upstream FATE suite has not been run. Multithreaded ffmpeg and libjxl are not validated.

### Preliminary Video Measurement

The first complete-file validation also measured total wall time, including startup, JIT, file I/O and hashing:

| Execution | Seconds | Maximum RSS KiB |
| --- | ---: | ---: |
| Native clang from the same linked bitcode | 190.53 | 251472 |
| .NET CIL | 250.96 | 330528 |

This is approximately 1.32x native elapsed time, not an optimized decoder benchmark. No native assembly acceleration is enabled in this comparison. There is no emcc/wasmtime comparison yet. Latest rerun measurements live under `artifacts/ffmpeg-browser/video-validation/` and may differ.

The complete `tests/all.sh spec-test` rerun also passed: 186.84 seconds native and 253.26 seconds CIL, about 1.36x, with all 1,150 frame hashes equal. A separate fresh-configure build passed the HDR check and contained 7,137 CIL method bodies with zero P/Invoke imports. Its count differs slightly from the earlier cached configuration because additional frontend feature probes now succeed.

## SPEC Development Runs

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

## Known Limits

- Not a complete compiler-driver replacement: unsupported native-linker flags and unresolved symbols are errors.
- No general `setjmp`/`longjmp`, LLVM dynamic stack save/restore, or computed-goto lowering yet.
- No LLVM TLS global lowering yet, despite managed pthread key support.
- Vector arithmetic, scalable vectors, binary128, and several wide-integer intrinsic families are not implemented. Auto-vectorization is disabled by default; generic `Vector<T>` acceleration is deferred.
- The C runtime is a tested subset, not complete glibc/POSIX. Locale, wide-character I/O, hexadecimal/long-double printf/scanf, full signal/process behavior, and some filesystem metadata are incomplete.
- C++ standard-library exports, exception pointer conversions, access/ambiguity corner cases, thread-local destruction, and complete library ABI coverage remain incomplete.
- Fortran derived types/finalization, complex/quad runtime operations, all array transformations, nondefault floating environments, unformatted/direct I/O, and full formatted/list-directed editing remain incomplete.
- OpenMP, MPI, and coarrays are not implemented. Speed/parallel benchmark coverage is therefore not claimed.
- The explicit scalar P/Invoke option remains limited. System mode adds tested C aggregate/varargs/data/callback bridges, but native C++ linkage, arbitrary callback signatures, native TLS globals and full native-runtime lifecycle remain incomplete.

Unsupported cases should remain visible diagnostics or explicit runtime errors, never silent native fallbacks or claims of successful emulation.