# SIMD128 Video Measurements

Measured on Linux x86-64, AMD Ryzen AI Max+ 395 (16 cores/32 logical processors), .NET runtime 10.0.11, SDK 10.0.400, LLVM/Clang 22.1.8. Native reference: the installed `/usr/bin/ffmpeg`, version 9.0.1-1, built with GCC 16.2.0. SIMD is opt-in with `FFMPEG_SIMD128=1`; see [simd128.md](simd128.md) for builds and supported operations.

## Method

- All six `/home/david/Videos/2026*.mp4` files, each 3840x2160 10-bit HEVC, decoded to EOF. Their combined decoded count is 2,829 frames. `20260607_181513.mp4` advertises 118 frames but all tested decoders output 117; measurements use actual completed frame counts.
- Four decoder threads, explicit frame threading, no hardware decoding, no autorotation, no audio/subtitle/data output, and no frame-rate resampling. Native ffmpeg retains its normal optimized CPU assembly. llvmnet builds use O1, no auto-vectorization, the same dependencies and decode harness, and only the SIMD profile selects the upstream wasm HEVC kernels.
- Three serial full-file runs per engine, alternating forward/reverse engine order. A twelve-frame preliminary run per engine exercises the path and warms file caches; each measured process is new, so JIT startup is still included. No concurrent builds or benchmark processes were launched during the timing sets. CPU affinity/frequency were not pinned.
- External GNU time records elapsed/user/system time and maximum RSS. Tables show medians of elapsed seconds; lower is better. `bench` mode skips pixel copies, MD5, and per-frame printing, but includes process startup, JIT where applicable, I/O, stream discovery, demuxing, decoding and cleanup. Installed native ffmpeg uses null output; its CLI overhead differs from the custom harness, so this is an end-to-end comparison, not isolated codec-kernel speed.
- Correctness was checked separately: every frame in every video from both SIMD JIT ABIs matched the installed native decoder's MD5 of active pixels. Baseline AOT/software-fallback correctness is covered by kernel and 8/10-bit media regressions. Host-targeted scalar/SIMD AOT also matched twelve frames from each supplied video before full-file timing; all timed runs must report matching final frame counts.

The native binary is a distribution build with a different revision/configuration and far broader CPU-specific optimization than the O1 translated build. These are not equivalent source/optimization pipelines. None of the modes uses a GPU. The supplied 10-bit clips use SIMD IDCT; 10-bit SAO, motion compensation, and other HEVC work remain scalar.

## JIT Before/After

Median seconds per full video, three runs:

| Video | Frames | Installed Native | System Scalar | System SIMD128 | Portable Scalar | Portable SIMD128 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 20260521_215039.mp4 | 1150 | 34.86 | 96.46 | 86.59 | 92.85 | 83.44 |
| 20260607_151708.mp4 | 531 | 14.20 | 38.65 | 36.09 | 37.89 | 34.83 |
| 20260607_152208.mp4 | 207 | 6.19 | 23.04 | 18.95 | 22.78 | 18.72 |
| 20260607_181513.mp4 | 117 | 5.22 | 13.05 | 12.01 | 12.69 | 11.80 |
| 20260628_154618.mp4 | 451 | 11.30 | 34.03 | 28.91 | 34.26 | 28.77 |
| 20260709_113931.mp4 | 373 | 10.55 | 32.17 | 28.08 | 31.21 | 27.73 |
| Sum of medians | 2829 | 82.32 | 237.40 | 210.63 | 231.68 | 205.29 |

Across the sum of per-video medians, SIMD increases throughput by **12.7% in system ABI** and **12.9% in portable ABI** (elapsed-time reductions of 11.3% and 11.4%). Portable SIMD reaches 13.78 fps over the combined workload versus 12.21 fps scalar and 34.37 fps native. It remains **2.49x native elapsed time**; system SIMD is 2.56x. Per-file portable SIMD throughput improvements range from about 7.5% to 21.7%.

## NativeAOT

The default baseline instruction-set build is correct but performs poorly on these SIMD kernels. Do not infer a speedup from successful AOT compilation:

| ABI | Baseline Scalar AOT Seconds | Baseline SIMD128 AOT Seconds | SIMD / Scalar Elapsed |
| --- | ---: | ---: | ---: |
| system | 238.89 | 531.15 | 2.22x |
| portable | 233.80 | 525.31 | 2.25x |

These are sums of the six per-video medians from the same 162-run set as the JIT table. An additional 90-run set used **explicit `--aot-instruction-set native`** for both before/after AOT builds, plus a fresh native reference. This targets the build host's CPU features; those executables are not promised to run on older CPUs or other architectures. The default remains baseline.

| Video | Installed Native | System Scalar AOT Host | System SIMD128 AOT Host | Portable Scalar AOT Host | Portable SIMD128 AOT Host |
| --- | ---: | ---: | ---: | ---: | ---: |
| 20260521_215039.mp4 | 34.69 | 96.08 | 83.23 | 92.84 | 81.60 |
| 20260607_151708.mp4 | 14.07 | 38.54 | 35.04 | 38.06 | 34.48 |
| 20260607_152208.mp4 | 6.22 | 22.64 | 18.37 | 22.53 | 18.15 |
| 20260607_181513.mp4 | 5.23 | 12.47 | 11.36 | 12.40 | 11.16 |
| 20260628_154618.mp4 | 11.82 | 35.20 | 29.44 | 34.82 | 28.50 |
| 20260709_113931.mp4 | 10.40 | 31.62 | 27.44 | 31.39 | 27.15 |
| Sum of medians | 82.43 | 236.55 | 204.88 | 232.04 | 201.04 |

Host-targeted SIMD AOT increases aggregate throughput by **15.5% in system ABI** and **15.4% in portable ABI**. Portable SIMD AOT host reaches 14.07 fps and remains 2.44x native elapsed time. These results do not justify silently selecting the build host's instruction set for deployable outputs.

## Reproduce

Build both scalar and SIMD profiles first. Matching dependencies are shared only within their ABI:

```sh
for runtime in system portable; do
    LLVMNET_RUNTIME="$runtime" bash scripts/build-dependencies.sh
    LLVMNET_RUNTIME="$runtime" FFMPEG_SIMD128=0 bash scripts/build-ffmpeg.sh
    LLVMNET_RUNTIME="$runtime" FFMPEG_SIMD128=1 bash scripts/build-ffmpeg.sh
done
```

Publish AOT variants, using the same linked bitcode and optimization preference on each side:

```sh
for runtime in system portable; do
    for simd in 0 1; do
        build="artifacts/ffmpeg-browser-$runtime"
        simd_args=()
        if [[ "$simd" == 1 ]]; then build+=-simd128; simd_args=(--simd128); fi
        bin/llvmnet --runtime="$runtime" "${simd_args[@]}" --nativeaot \
            "$build/decode.bc" -o "$build/decode-aot"
        bin/llvmnet --runtime="$runtime" "${simd_args[@]}" --nativeaot --aot-instruction-set native \
            "$build/decode.bc" -o "$build/decode-aot-host"
    done
done
```

The benchmark uses Node.js and GNU time. With no filenames it enumerates the requested `2026*.mp4` pattern; explicit filenames can also be supplied. Run validation separately from measurement:

```sh
VIDEO_BENCH_OUTPUT=artifacts/video-simd128-validation \
VIDEO_BENCH_ENGINES=native,system-simd128,portable-simd128 \
node scripts/benchmark-simd128.mjs --validate-only

VIDEO_BENCH_OUTPUT=artifacts/video-simd128-performance VIDEO_BENCH_REPEATS=3 \
node scripts/benchmark-simd128.mjs --measure-only

VIDEO_BENCH_OUTPUT=artifacts/video-simd128-aot-host-performance \
VIDEO_BENCH_ENGINES=native,system-scalar-aot-host,system-simd128-aot-host,portable-scalar-aot-host,portable-simd128-aot-host \
node scripts/benchmark-simd128.mjs --measure-only
```

Omitting both mode switches performs hash validation then measurements. `--measure-only` deliberately does not repeat hashes; use it only after preparing/validating the same binaries. `VIDEO_BENCH_THREADS` defaults to 4, `VIDEO_BENCH_REPEATS` to 3, `VIDEO_BENCH_TIMEOUT` to 1800 seconds per child process, and `VIDEO_BENCH_FRAMES`/`VIDEO_BENCH_VERIFY_FRAMES` to 0 (whole file). Frame-limited timings must be labeled as such. The default engine set is installed native plus system/portable scalar/SIMD JIT and baseline AOT; host-targeted AOT engines require explicit selection. `VIDEO_BENCH_NATIVE` can select a different native executable, whose version is recorded.

Each output directory retains metadata (machine, native version, stream information and settings), exact measured commands, per-run stdout/stderr, wall/user/system/RSS records, frame counts, medians and ranges, and a completion record. The recorded full-file validation is under `artifacts/video-simd128-validation/`, the 162-run JIT/baseline-AOT set under `artifacts/video-simd128-performance/`, and the 90-run host-targeted set under `artifacts/video-simd128-aot-host-performance/`. Raw data are ignored build artifacts; this document records the measured snapshot.