#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source_dir="${FFMPEG_SOURCE:-/mnt/storage/david/Development/LightPlayer-Packages/ffmpeg}"
output="$root/artifacts/tests/ffmpeg-simd128"
mkdir -p "$output"
for runtime in system portable; do
    build="$root/artifacts/ffmpeg-browser-$runtime-simd128"
    if [[ "${FFMPEG_SIMD128_SKIP_BUILD:-0}" != 1 ]]; then
        LLVMNET_RUNTIME="$runtime" bash "$root/scripts/build-dependencies.sh" >"$output/deps-$runtime.log" 2>&1
        LLVMNET_RUNTIME="$runtime" FFMPEG_SIMD128=1 FFMPEG_BUILD_DIR="$build" \
            bash "$root/scripts/build-ffmpeg.sh" >"$output/build-$runtime.log" 2>&1
    fi
    grep -q '^#define HAVE_SIMD128 1$' "$build/config.h"
    test "$(<"$build/llvmnet-runtime")" == "$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" --simd128 -O2 -I"$build" -I"$source_dir" \
        "$root/tests/ffmpeg/simd128.c" "$build/libavcodec/libavcodec.a" "$build/libavutil/libavutil.a" \
        --keep-ir "$output/dsp-$runtime.bc" -o "$output/dsp-$runtime.dll"
    dotnet "$output/dsp-$runtime.dll"
    DOTNET_EnableHWIntrinsic=0 dotnet "$output/dsp-$runtime.dll"
    "$root/bin/llvmnet" --runtime="$runtime" --simd128 --nativeaot "$output/dsp-$runtime.bc" -o "$output/dsp-$runtime-aot" \
        >"$output/dsp-$runtime-aot.log" 2>&1
    "$output/dsp-$runtime-aot"
done
if [[ "${1:-}" == --dsp-only ]]; then exit 0; fi
ffmpeg -nostdin -v error -f lavfi -i 'testsrc2=size=320x192:rate=12:duration=2' \
    -c:v libx265 -threads 4 -x265-params 'pools=4:frame-threads=2:wpp=1:log-level=error' -y "$output/hevc8.mp4"
ffmpeg -nostdin -v error -f lavfi -i 'testsrc2=size=320x192:rate=12:duration=2' -pix_fmt yuv420p10le \
    -c:v libx265 -threads 4 -x265-params 'pools=4:frame-threads=2:wpp=1:log-level=error' -y "$output/hevc10.mp4"
for runtime in system portable; do
    build="$root/artifacts/ffmpeg-browser-$runtime-simd128"
    "$root/bin/llvmnet" --runtime="$runtime" --simd128 --nativeaot "$build/decode.bc" -o "$build/decode-aot" \
        >"$output/decode-$runtime-aot.log" 2>&1
done
for depth in 8 10; do
    fixture="$output/hevc$depth.mp4"
    "$root/artifacts/ffmpeg-browser-system/decode-native" "$fixture" 1000 video 1 >"$output/baseline-$depth.txt"
    grep -q '^frames=24$' "$output/baseline-$depth.txt"
    for runtime in system portable; do
        build="$root/artifacts/ffmpeg-browser-$runtime-simd128"
        for mode in frame slice; do
            for engine in jit aot software; do
                runner=(dotnet "$build/decode.dll")
                if [[ "$engine" == aot ]]; then runner=("$build/decode-aot"); fi
                if [[ "$engine" == software ]]; then runner=(env DOTNET_EnableHWIntrinsic=0 dotnet "$build/decode.dll"); fi
                label="$depth-$runtime-$mode-$engine"
                timeout 120s "${runner[@]}" "$fixture" 1000 video 4 "$mode" >"$output/$label.txt" 2>"$output/$label.log"
                diff -u "$output/baseline-$depth.txt" "$output/$label.txt"
                timeout 120s "${runner[@]}" "$fixture" 1000 video 4 "$mode" bench >"$output/$label-bench.txt" 2>"$output/$label-bench.log"
                test "$(<"$output/$label-bench.txt")" == frames=24
                printf 'PASS: %s HEVC SIMD hashes and decode-only mode\n' "$label"
            done
        done
    done
done
dotnet build "$root/tests/LlvmNet.Checks" -c Release --nologo
dotnet "$root/tests/LlvmNet.Checks/bin/Release/net10.0/LlvmNet.Checks.dll" inspect \
    "$root/artifacts/ffmpeg-browser-portable-simd128/decode.dll" | tee "$output/portable-inspect.txt"
grep -q ', 0 explicit P/Invoke imports\.$' "$output/portable-inspect.txt"