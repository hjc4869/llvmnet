# Evidence: SV-20, the SIMD gates (2026-11-05)

Host: AMD Ryzen AI MAX+ 395 (Zen 5, AVX-512), Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `a29f1b1`. Windows x64: AMD Ryzen 9 9955HX (`fluorite`); macOS: Apple M6 (`mainmac`); Linux ARM64: the arm64 .NET container under qemu. Plan: [12-simd.md](../plan/12-simd.md#testing).

## What runs where

| Gate | Test or job | Linux x64 | Windows x64, macOS ARM64, Linux ARM64 |
| --- | --- | --- | --- |
| Vector semantics probes (SV-04) | `vector-semantics` (core), `vector-fuzz` (fuzz, 10 seeds a night) | every pull request and nightly | nightly and on every pull request's bundle: `vector-integer`, `vector-float` at 128 bits under the JIT and as NativeAOT, at 256 bits and without hardware intrinsics under the JIT (new) |
| x86 intrinsic conformance (SV-08, SV-09) | `x86-intrinsics` (core) | every pull request | `x86-sse-conformance`, `x86-avx-conformance` in the bundle, JIT and NativeAOT (since SV-08, SV-09) |
| Vector code and reductions (SV-02, SV-21) | `vector-lowering`, `vector-sweep`, `vector-reductions` (core) | every pull request | `vector-extensions-O0`, `-O2`, `vector-loops`, `vector-reductions`, `vector-reductions-v3` in the bundle |
| ffmpeg SIMD128 profile (SV-07) | `ffmpeg-simd128` (integration) | nightly | `ffmpeg-simd128-hevc8`, `-hevc10` in the nightly's bundle, JIT and NativeAOT (new) |
| ffmpeg nasm profile from a kernel package (SV-13) | `ffmpeg-x86asm` (integration) | nightly | `ffmpeg-x86asm-hevc8`, `-hevc10`, `-h264` in the nightly's bundle: the C paths on ARM64, skipped on Windows x64 (new) |
| Native kernel packages, CPU detection (SV-12, SV-13) | `native-kernels`, `cpu-detection` (core) | every pull request | `kernels`, `cpu-detection` in the bundle |
| SIMD kernels per ISA level (SV-20) | the benchmark harness's `simd` group, `--vectorized` | nightly benchmark table; `benchmarks` (core) validates `sad8` at each level | |

## The bundle

`tests/xp/build.sh` now adds the vector probes: `tests/semantics/vectors.py`'s two modules (seed 20261103, 22,377 cases), compiled natively for the expected output and translated with every translatable function typed, as six fixtures. `tests/xp/ffmpeg.sh`, which the new serial integration test `tests/xp-ffmpeg.sh` runs after `xp`, `ffmpeg-simd128` and `ffmpeg-x86asm`, translates both browser-profile decoders again from their builds' bitcode (so they match the bundle's runtime), generates three 24-frame clips (HEVC 8- and 10-bit, H.264) into the bundle, and records the native decoder's frame hashes; each decode runs with four frame threads. Two decoders serve five fixtures: the manifest's new sixth column names the image a fixture runs, and `run.sh` publishes each NativeAOT project once. The nasm decoder's fixtures need `kernels:linux-x64`, which `run.sh` reads as "available where the host is not x64 or is an x64 host the package has a library for": on Windows x64 ffmpeg's x86 detection reports AVX2 and would call kernels the package has no library for. Clip arguments are `{bundle}/data/...`, which `run.sh` replaces with a path relative to the fixture's work directory: Git Bash turned the absolute `/c/...` into `C:/...`, which ffmpeg read as a URL with the protocol `C` (`AVERROR_PROTOCOL_NOT_FOUND`), the first Windows run's four failures. The bundle grows from 15 to 46 MB.

The whole bundle (74 fixtures with the ffmpeg ones):

| Host | Result |
| --- | --- |
| Linux x64 | the 13 new fixtures pass under the JIT and the 9 with NativeAOT projects as NativeAOT (`run.sh --aot --only ...`); `tests/xp-ffmpeg.sh` passes |
| macOS ARM64 (M6) | 99 passed, 4 skipped (GLES and Vulkan, no host library), JIT and NativeAOT, the nasm decoder through its C paths |
| Windows x64 | 93 passed, 10 skipped (GLES; Vulkan without a driver; the three nasm decodes, by the `kernels:` rule), JIT and NativeAOT, after the relative-path change (the SIMD128 decodes ran again: 4 passed) |
| Linux ARM64 (JIT) | 72 passed, 2 skipped (GLES, Vulkan) |

## The benchmark table

`scripts/benchmark.py --group simd --vectorized --repeats 3`, the first measurement (seconds, median; ratios to vectorized native at the level; no baseline yet, so reported as new):

| Kernel | Native vectorized: baseline, v2, v3 | JIT: baseline, v2, v3 | NativeAOT: baseline, v2, v3 |
| --- | --- | --- | --- |
| saxpy | 0.192, 0.185, 0.090 | 1.48, 1.57, 1.89 | 1.28, 1.40, 1.55 |
| i32mix | 0.226, 0.221, 0.093 | 1.18, 1.20, 1.74 | 1.63, 1.21, 1.31 |
| sad8 | 0.118, 0.115, 0.055 | 2.20, 2.23, 2.94 | 1.38, 1.92, 2.35 |
| dot16 | 0.152, 0.161, 0.066 | 1.41, 1.35, 2.31 | 1.07, 1.00, 1.64 |
| max8 | 0.038, 0.027, 0.015 | 2.24, 2.92, 4.62 | 1.48, 1.32, 1.00 |
| fdot8 | 0.197, 0.168, 0.155 | 1.30, 1.35, 1.37 | 1.17, 1.32, 1.15 |
| geometric mean | | 1.58, 1.68, 2.28 | 1.32, 1.34, 1.44 |

The JIT columns of these single-call kernels carry the on-stack-replacement placement effect the [SV-21 evidence](2026-11-05-widening-reductions.md#the-surveys-kernels) describes; NativeAOT is the steadier measure. One case for later: `sad8` at `x86-64-v2` takes 0.221 s as NativeAOT against 0.163 at the baseline, with the same IR and the same `psadbw` recognition, so the difference is ILC's code for the same CIL at that instruction set. At `x86-64-v3` native code doubles its speed with AVX2 and llvmnet follows by less, most where the 256-bit reductions and widenings are compositions (`sad8`, `dot16`).

## What it does not show

- The CI runs themselves: the workflow did not change (the nightly's all-tags step selects `xp-ffmpeg`, and the bundle it uploads is the one the `xp` jobs download), and its first run on GitHub is the confirmation.
- A Windows x64 kernel package for ffmpeg (nasm's `win64` format with Windows' calling convention, SV-16's territory), so the nasm decoder's kernels run on Linux x64 only.
