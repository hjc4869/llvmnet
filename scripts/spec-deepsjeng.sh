#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
media="${SPEC2017_MEDIA:-/mnt/cpu2017}"
stage="$root/artifacts/spec2017"
benchmark="$stage/benchspec/CPU/531.deepsjeng_r"
dataset="${1:-test}"
case "$dataset" in test|train|refrate) ;; *) printf 'Usage: %s [test|train|refrate]\n' "$0" >&2; exit 2 ;; esac
if [[ ! -f "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" ]]; then
    printf 'Complete the full-video ffmpeg validation first.\n' >&2
    exit 1
fi
diff -q "$root/artifacts/ffmpeg-browser/video-validation/native-frames.txt" "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" >/dev/null
mkdir -p "$stage"
if [[ ! -d "$benchmark/src" ]]; then
    archives=("$media"/install_archives/benchball/531.deepsjeng_r-*.tar.xz)
    tar --no-same-owner -xJf "${archives[0]}" -C "$stage"
fi
source_dir="$benchmark/src"
build="$stage/llvmnet-deepsjeng"
mkdir -p "$build/native-$dataset" "$build/cil-$dataset"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
sources=(attacks.cpp bitboard.cpp bits.cpp board.cpp draw.cpp endgame.cpp epd.cpp generate.cpp initp.cpp make.cpp moves.cpp neval.cpp pawn.cpp preproc.cpp search.cpp see.cpp sjeng.cpp state.cpp ttable.cpp utils.cpp)
paths=()
for file in "${sources[@]}"; do paths+=("$source_dir/$file"); done
"$root/bin/llvmnet" -O1 -std=gnu++14 -DSPEC -DSPEC_CPU -DSPEC_CPU_LP64 -DSMALL_MEMORY -DSPEC_AUTO_SUPPRESS_OPENMP \
    -I"$source_dir" "${paths[@]}" -lm --keep-ir "$build/deepsjeng.bc" -o "$build/deepsjeng.dll"
clang++-22 -O1 "$build/deepsjeng.bc" -lm -o "$build/deepsjeng-native"
for input in "$benchmark/data/$dataset/input/"*.txt; do
    name="$(basename "$input" .txt)"
    "$build/deepsjeng-native" "$input" >"$build/native-$dataset/$name.out" 2>"$build/native-$dataset/$name.err"
    dotnet "$build/deepsjeng.dll" "$input" >"$build/cil-$dataset/$name.out" 2>"$build/cil-$dataset/$name.err"
    diff -u "$benchmark/data/$dataset/output/$name.out" "$build/native-$dataset/$name.out"
    diff -u "$build/native-$dataset/$name.out" "$build/cil-$dataset/$name.out"
done
printf 'PASS: CPU 2017 531.deepsjeng_r %s, one process; native/CIL/supplied outputs match. Development run, not a SPEC score.\n' "$dataset"