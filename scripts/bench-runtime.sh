#!/usr/bin/env bash
# Runtime performance guard (LC-24): the microbenchmarks in tests/perf (stdio, malloc/free, printf) built in portable
# mode with this toolchain and with a baseline toolchain, by default the hand-written C library of 0f21ada (end of
# M2), which the script extracts from git and builds. Each runs under JIT and NativeAOT; wall times are the median of
# three runs, with native glibc builds for reference, and every build's output must equal the native one. Run it on
# an otherwise idle machine.
#   scripts/bench-runtime.sh [baseline-toolchain-root]
#   BENCH_VIDEO=video.mp4 BENCH_FRAMES=300                 also time an ffmpeg decode of that many frames with
#   BENCH_FFMPEG_BASELINE=dir BENCH_FFMPEG=dir             two ffmpeg builds (directories holding decode.dll)
# Results: artifacts/perf/runtime.tsv
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
baseline="${1:-$root/artifacts/perf/toolchain-0f21ada}"
if [[ ! -x "$baseline/bin/llvmnet" ]]; then
    mkdir -p "$baseline"
    git -C "$root" archive 0f21ada | tar -x -C "$baseline"
    bash "$baseline/scripts/ensure-built.sh" >/dev/null
fi
output="$root/artifacts/perf/runtime"
rm -rf "$output"
# Each toolchain copies its own LlvmNet.Runtime.dll next to its programs, so the two need separate directories.
mkdir -p "$output/baseline" "$output/current"

# Median wall time in seconds of three runs of a command, whose standard output and error are discarded.
median() {
    local times=() start
    for _ in 1 2 3; do
        start=$EPOCHREALTIME
        "$@" >/dev/null 2>&1
        times+=("$(awk -v start="$start" -v end="$EPOCHREALTIME" 'BEGIN { printf "%.2f", end - start }')")
    done
    printf '%s\n' "${times[@]}" | sort -n | sed -n 2p
}

printf 'benchmark\tnative\tbaseline JIT\tcurrent JIT\tbaseline NativeAOT\tcurrent NativeAOT\tcurrent/baseline JIT\tcurrent/baseline NativeAOT\n' >"$root/artifacts/perf/runtime.tsv"
for benchmark in fwrite malloc printf; do
    source_file="$root/tests/perf/$benchmark.c"
    clang-22 -O2 -w "$source_file" -o "$output/$benchmark-native"
    for variant in baseline current; do
        compiler="$root/bin/llvmnet"
        [[ "$variant" == baseline ]] && compiler="$baseline/bin/llvmnet"
        "$compiler" --runtime=portable -O2 -w "$source_file" -o "$output/$variant/$benchmark.dll" >"$output/$variant/$benchmark.log" 2>&1
        "$compiler" --runtime=portable -O2 -w --nativeaot "$source_file" -o "$output/$variant/$benchmark-aot" >>"$output/$variant/$benchmark.log" 2>&1
    done
    declare -A seconds=()
    for build in native baseline current baseline-aot current-aot; do
        case "$build" in
            native) command=("$output/$benchmark-native") ;;
            baseline | current) command=(dotnet "$output/$build/$benchmark.dll") ;;
            *) command=("$output/${build%-aot}/$benchmark-aot") ;;
        esac
        [[ "$benchmark" == fwrite ]] && command+=("$output/$benchmark-$build.data")
        # printf's lines go to /dev/null and its checksum to stderr; the others print a checksum.
        if [[ "$benchmark" == printf ]]; then "${command[@]}" >/dev/null 2>"$output/$benchmark-$build.out"
        else "${command[@]}" >"$output/$benchmark-$build.out"; fi
        if ! cmp -s "$output/$benchmark-native.out" "$output/$benchmark-$build.out"; then
            printf 'FAIL: %s %s output differs from native\n' "$benchmark" "$build" >&2
            exit 1
        fi
        seconds[$build]="$(median "${command[@]}")"
    done
    awk -v name="$benchmark" -v native="${seconds[native]}" -v jit0="${seconds[baseline]}" -v jit1="${seconds[current]}" \
        -v aot0="${seconds[baseline-aot]}" -v aot1="${seconds[current-aot]}" \
        'BEGIN { printf "%s\t%s\t%s\t%s\t%s\t%s\t%.2f\t%.2f\n", name, native, jit0, jit1, aot0, aot1, jit1 / jit0, aot1 / aot0 }' \
        >>"$root/artifacts/perf/runtime.tsv"
done
if [[ -n "${BENCH_VIDEO:-}" ]]; then
    frames="${BENCH_FRAMES:-300}"
    old="$(median dotnet "${BENCH_FFMPEG_BASELINE:?}/decode.dll" "$BENCH_VIDEO" "$frames")"
    new="$(median dotnet "${BENCH_FFMPEG:?}/decode.dll" "$BENCH_VIDEO" "$frames")"
    awk -v name="ffmpeg decode, $frames frames" -v jit0="$old" -v jit1="$new" \
        'BEGIN { printf "%s\t\t%s\t%s\t\t\t%.2f\t\n", name, jit0, jit1, jit1 / jit0 }' >>"$root/artifacts/perf/runtime.tsv"
fi
column -t -s $'\t' "$root/artifacts/perf/runtime.tsv"
