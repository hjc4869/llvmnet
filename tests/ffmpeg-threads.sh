#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/ffmpeg-threads"
mkdir -p "$output"
timeout_seconds="${FFMPEG_THREADS_TIMEOUT:-120}"
engines=(jit)
if [[ "${FFMPEG_THREADS_AOT:-0}" == 1 ]]; then engines+=(aot); fi
for runtime in system portable; do
    build="$root/artifacts/ffmpeg-browser-$runtime"
    if [[ "${FFMPEG_THREADS_SKIP_BUILD:-0}" != 1 ]]; then
        LLVMNET_RUNTIME="$runtime" LLVMNET_DEPS_PREFIX="$root/artifacts/deps-$runtime" \
            LLVMNET_DEPS_BUILD_DIR="$root/artifacts/deps-build-$runtime" \
            bash "$root/scripts/build-dependencies.sh" >"$output/dependencies-$runtime.log" 2>&1
        LLVMNET_RUNTIME="$runtime" LLVMNET_DEPS_PREFIX="$root/artifacts/deps-$runtime" FFMPEG_BUILD_DIR="$build" \
            bash "$root/scripts/build-ffmpeg.sh" >"$output/build-$runtime.log" 2>&1
    fi
    test "$(<"$build/llvmnet-runtime")" == "$runtime"
    grep -q '^#define HAVE_PTHREADS 1$' "$build/config.h"
    if [[ "${FFMPEG_THREADS_AOT:-0}" == 1 ]]; then
        "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$build/decode.bc" -o "$build/decode-aot" \
            >"$output/aot-$runtime.log" 2>&1
    fi
done
dotnet build "$root/tests/LlvmNet.Checks" -c Release --nologo
dotnet "$root/tests/LlvmNet.Checks/bin/Release/net10.0/LlvmNet.Checks.dll" inspect \
    "$root/artifacts/ffmpeg-browser-portable/decode.dll" | tee "$output/portable-inspect.txt"
grep -q ', 0 explicit P/Invoke imports\.$' "$output/portable-inspect.txt"
source='testsrc2=size=320x192:rate=12:duration=2'
if [[ ! -f "$output/h264.mp4" ]]; then
    ffmpeg -nostdin -v error -f lavfi -i "$source" -c:v libx264 -threads 4 \
        -x264-params 'slices=4:bframes=3:keyint=24:scenecut=0' -y "$output/h264.mp4"
fi
if [[ ! -f "$output/hevc.mp4" ]]; then
    ffmpeg -nostdin -v error -f lavfi -i "$source" -c:v libx265 -threads 4 -pix_fmt yuv420p10le \
        -x265-params 'pools=4:frame-threads=2:wpp=1:log-level=error:keyint=24' -y "$output/hevc.mp4"
fi
if [[ ! -f "$output/vp9.webm" ]]; then
    ffmpeg -nostdin -v error -f lavfi -i "$source" -c:v libvpx-vp9 -threads 4 \
        -cpu-used 8 -row-mt 1 -tile-columns 1 -y "$output/vp9.webm"
fi
if [[ ! -f "$output/av1.mkv" ]]; then
    ffmpeg -nostdin -v error -f lavfi -i "$source" -c:v libaom-av1 -threads 4 \
        -cpu-used 8 -row-mt 1 -tiles 2x1 -y "$output/av1.mkv"
fi
cases=(h264.mp4:frame:1:h264 h264.mp4:slice:2:h264 hevc.mp4:frame:1:hevc
       hevc.mp4:slice:2:hevc vp9.webm:frame:1:vp9 av1.mkv:auto:0:libdav1d)
for entry in "${cases[@]}"; do
    IFS=: read -r fixture mode active codec <<<"$entry"
    for limit in 1000 3; do
        expected=24
        if [[ "$limit" == 3 ]]; then expected=3; fi
        baseline="$output/$fixture-$mode-$limit-baseline.txt"
        timeout "${timeout_seconds}s" "$root/artifacts/ffmpeg-browser-system/decode-native" \
            "$output/$fixture" "$limit" video 1 "$mode" >"$baseline" 2>"$baseline.log"
        grep -q "^frames=$expected$" "$baseline"
        for threads in 2 4; do
            label="$fixture-$mode-$limit-$threads"
            timeout "${timeout_seconds}s" "$root/artifacts/ffmpeg-browser-system/decode-native" \
                "$output/$fixture" "$limit" video "$threads" "$mode" >"$output/$label-native.txt" 2>"$output/$label-native.log"
            diff -u "$baseline" "$output/$label-native.txt"
            grep -q "^decoder=$codec threads=$threads thread_type=$active$" "$output/$label-native.log"
            for runtime in system portable; do
                build="$root/artifacts/ffmpeg-browser-$runtime"
                for engine in "${engines[@]}"; do
                    runner=(dotnet "$build/decode.dll")
                    if [[ "$engine" == aot ]]; then runner=("$build/decode-aot"); fi
                    timeout "${timeout_seconds}s" "${runner[@]}" "$output/$fixture" "$limit" video "$threads" "$mode" \
                        >"$output/$label-$runtime-$engine.txt" 2>"$output/$label-$runtime-$engine.log"
                    diff -u "$baseline" "$output/$label-$runtime-$engine.txt"
                    grep -q "^decoder=$codec threads=$threads thread_type=$active$" "$output/$label-$runtime-$engine.log"
                    printf 'PASS: %s %s %s threads=%s mode=%s frames=%s\n' "$runtime" "$engine" "$fixture" "$threads" "$mode" "$expected"
                done
            done
        done
    done
done
printf 'PASS: threaded ffmpeg hashes, frame/slice activation, decoder drain and early teardown in both ABIs\n'