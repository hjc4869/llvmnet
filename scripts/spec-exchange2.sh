#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
media="${SPEC2017_MEDIA:-/mnt/cpu2017}"
stage="$root/artifacts/spec2017"
benchmark="$stage/benchspec/CPU/548.exchange2_r"
dataset="${1:-test}"
case "$dataset" in test|train|refrate) ;; *) printf 'Usage: %s [test|train|refrate]\n' "$0" >&2; exit 2 ;; esac
if [[ ! -f "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" ]]; then
    printf 'Complete the full-video ffmpeg validation first.\n' >&2
    exit 1
fi
diff -q "$root/artifacts/ffmpeg-browser/video-validation/native-frames.txt" "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" >/dev/null
mkdir -p "$stage"
if [[ ! -d "$benchmark/src" ]]; then
    archives=("$media"/install_archives/benchball/548.exchange2_r-*.tar.xz)
    tar --no-same-owner -xJf "${archives[0]}" -C "$stage"
fi
build="$stage/llvmnet-exchange2"
mkdir -p "$build/native-$dataset" "$build/cil-$dataset"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
"$root/bin/llvmnet" -O1 -DSPEC -DSPEC_CPU -DSPEC_CPU_LP64 -DSPEC_AUTO_SUPPRESS_OPENMP \
    "$benchmark/src/exchange2.F90" --keep-ir "$build/exchange2.bc" -o "$build/exchange2.dll"
flang-22 -O1 "$build/exchange2.bc" -o "$build/exchange2-native"
for runtime in native cil; do
    run="$build/$runtime-$dataset"
    if [[ -d "$benchmark/data/all/input" ]]; then
        cp -a "$benchmark/data/all/input/." "$run/"
    fi
    cp -a "$benchmark/data/$dataset/input/." "$run/"
    command=("$build/exchange2-native")
    if [[ "$runtime" == cil ]]; then command=(dotnet "$build/exchange2.dll"); fi
    (
        cd "$run"
        while read -r count rest; do
            [[ -z "$count" || "$count" == \#* ]] && continue
            "${command[@]}" "$count" >exchange2.txt 2>exchange2.err
        done < control
    )
done
for expected in "$benchmark/data/$dataset/output/"*; do
    name="$(basename "$expected")"
    diff -u "$expected" "$build/native-$dataset/$name"
    diff -u "$build/native-$dataset/$name" "$build/cil-$dataset/$name"
done
diff -u "$build/native-$dataset/exchange2.txt" "$build/cil-$dataset/exchange2.txt"
printf 'PASS: CPU 2017 548.exchange2_r %s, one process; native/CIL/supplied outputs match. Development run, not a SPEC score.\n' "$dataset"