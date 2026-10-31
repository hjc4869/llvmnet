# SIMD128 video measurements

`scripts/benchmark-simd128.mjs` measures whole-file HEVC decoding with the scalar and SIMD128 ffmpeg builds of both runtime modes against a native ffmpeg executable, after checking that every engine decodes the same frames. [user/simd128.md](../user/simd128.md) describes the SIMD128 profile.

## Method

- **Engines.** `native` is the ffmpeg command-line tool (`VIDEO_BENCH_NATIVE`, default `/usr/bin/ffmpeg`). For each of `system` and `portable` there are `<mode>-scalar` and `<mode>-simd128` under JIT, the same with `-aot` (NativeAOT, baseline instruction set) and with `-aot-host` (NativeAOT built with `--aot-instruction-set native`). The default set is every engine except the `-aot-host` ones; `VIDEO_BENCH_ENGINES` selects a comma-separated list.
- **Workload.** Every engine decodes the first video stream of each file to the end with four decoder threads (`VIDEO_BENCH_THREADS`) and frame threading. The llvmnet builds run the decode harness in `bench` mode, which skips hashing and per-frame output; native ffmpeg decodes in software to the null muxer. `VIDEO_BENCH_FRAMES` limits the frames.
- **Validation.** The MD5 of every decoded frame of each llvmnet engine must equal native ffmpeg's `framemd5` output (`VIDEO_BENCH_VERIFY_FRAMES` limits the frames). Measured runs must report the same frame count.
- **Timing.** A twelve-frame run per engine warms the file cache. Each video is then decoded `VIDEO_BENCH_REPEATS` times (default 3) per engine, one process at a time, alternating forward and reverse engine order. Every measured process is new, so times include process startup and, for JIT engines, compilation. GNU time records elapsed, user and system time and peak RSS; the summary reports median elapsed seconds and frames per second. `VIDEO_BENCH_TIMEOUT` bounds each process (default 1800 seconds).

The comparison with native ffmpeg is end to end: the native tool uses its hand-written CPU assembly, while the llvmnet builds are generic C at `-O1` without auto-vectorization, plus ffmpeg's WebAssembly HEVC IDCT and SAO kernels in the SIMD128 profile. `-aot-host` executables use the build host's CPU features and may not run on other CPUs.

## Running

Build the scalar and SIMD128 profiles in both modes, then the NativeAOT variants from the same linked bitcode:

```sh
for runtime in system portable; do
    LLVMNET_RUNTIME="$runtime" bash scripts/build-dependencies.sh
    LLVMNET_RUNTIME="$runtime" FFMPEG_SIMD128=0 bash scripts/build-ffmpeg.sh
    LLVMNET_RUNTIME="$runtime" FFMPEG_SIMD128=1 bash scripts/build-ffmpeg.sh
done
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

The script takes video files as arguments, or every `.mp4` file in the directory `LLVMNET_LOCAL_MEDIA`. It needs Node.js, GNU time at `/usr/bin/time` and `/usr/bin/ffprobe`. Without a mode switch it validates and then measures; `--validate-only` and `--measure-only` run one step, and `--measure-only` should only follow a validation of the same builds.

```sh
VIDEO_BENCH_OUTPUT=artifacts/video-simd128-validation \
VIDEO_BENCH_ENGINES=native,system-simd128,portable-simd128 \
node scripts/benchmark-simd128.mjs --validate-only video.mp4

VIDEO_BENCH_OUTPUT=artifacts/video-simd128-performance \
node scripts/benchmark-simd128.mjs --measure-only video.mp4

VIDEO_BENCH_OUTPUT=artifacts/video-simd128-aot-host-performance \
VIDEO_BENCH_ENGINES=native,system-scalar-aot-host,system-simd128-aot-host,portable-scalar-aot-host,portable-simd128-aot-host \
node scripts/benchmark-simd128.mjs --measure-only video.mp4
```

The output directory (default `artifacts/video-simd128/<time>`) holds `metadata.json` (machine, native ffmpeg and .NET versions, stream information, settings), the frame hashes, `validation.json`, every run's output and timing, `results.json`, `summary.json`, `summary.md` and `completion.json`. Recorded results belong in `docs/evidence/`.
