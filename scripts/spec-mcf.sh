#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
media="${SPEC2017_MEDIA:-/mnt/cpu2017}"
stage="$root/artifacts/spec2017"
benchmark="$stage/benchspec/CPU/505.mcf_r"
dataset="${1:-test}"
case "$dataset" in test|train|refrate) ;; *) printf 'Usage: %s [test|train|refrate]\n' "$0" >&2; exit 2 ;; esac
if [[ ! -f "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" ]]; then
    printf 'Complete the full-video ffmpeg validation before running SPEC development tests.\n' >&2
    exit 1
fi
diff -q "$root/artifacts/ffmpeg-browser/video-validation/native-frames.txt" "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" >/dev/null
mkdir -p "$stage"
if [[ ! -d "$benchmark/src" ]]; then
    archives=("$media"/install_archives/benchball/505.mcf_r-*.tar.xz)
    if [[ ${#archives[@]} -ne 1 || ! -f "${archives[0]}" ]]; then
        printf 'Expected one mcf archive in %s\n' "$media" >&2
        exit 1
    fi
    tar --no-same-owner -xJf "${archives[0]}" -C "$stage"
fi
source_dir="$benchmark/src"
build="$stage/llvmnet-mcf"
mkdir -p "$build/native-$dataset" "$build/cil-$dataset"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
sources=(mcf.c mcfutil.c readmin.c implicit.c pstart.c output.c treeup.c pbla.c pflowup.c psimplex.c pbeampp.c spec_qsort/spec_qsort.c)
paths=()
for file in "${sources[@]}"; do paths+=("$source_dir/$file"); done
"$root/bin/llvmnet" -O1 -DSPEC -DSPEC_CPU -DSPEC_CPU_LP64 -DSPEC_AUTO_SUPPRESS_OPENMP \
    -I"$source_dir" -I"$source_dir/spec_qsort" "${paths[@]}" -lm --keep-ir "$build/mcf.bc" -o "$build/mcf.dll"
clang-22 -O1 "$build/mcf.bc" -lm -o "$build/mcf-native"
for runtime in native cil; do
    run="$build/$runtime-$dataset"
    cp -a "$benchmark/data/$dataset/input/." "$run/"
    command=("$build/mcf-native")
    if [[ "$runtime" == cil ]]; then command=(dotnet "$build/mcf.dll"); fi
    (
        cd "$run"
        while IFS= read -r input; do
            [[ -z "$input" ]] && continue
            "${command[@]}" "$input" >"${input%.in}.out" 2>"${input%.in}.err"
        done < control
    )
done
for expected in "$benchmark/data/$dataset/output/"*.out; do
    file="$(basename "$expected")"
    diff -u "$expected" "$build/native-$dataset/$file"
    diff -u "$build/native-$dataset/$file" "$build/cil-$dataset/$file"
done
printf 'PASS: CPU 2017 505.mcf_r, %s input, one process; native/CIL/supplied outputs match. Development test, not a SPEC score.\n' "$dataset"