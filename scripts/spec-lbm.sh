#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
suite="${1:-2017}"
dataset="${2:-test}"
case "$suite" in
    2017) media="${SPEC2017_MEDIA:-/mnt/cpu2017}"; number=519; flags=(-DSPEC_AUTO_SUPPRESS_OPENMP) ;;
    2026) media="${SPEC2026_MEDIA:-/mnt/cpu2026}"; number=782; flags=(-DSPEC_AUTO_SUPPRESS_THREADING -DSPEC_RATE) ;;
    *) printf 'Usage: %s [2017|2026] [test|train|refrate]\n' "$0" >&2; exit 2 ;;
esac
case "$dataset" in test|train|refrate) ;; *) exit 2 ;; esac
if [[ ! -f "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" ]]; then
    printf 'Complete the full-video ffmpeg validation first.\n' >&2
    exit 1
fi
diff -q "$root/artifacts/ffmpeg-browser/video-validation/native-frames.txt" "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" >/dev/null
stage="$root/artifacts/spec$suite"
benchmark="$stage/benchspec/CPU/$number.lbm_r"
mkdir -p "$stage"
if [[ ! -d "$benchmark/src" ]]; then
    archives=("$media/install_archives/benchball/$number.lbm_r-"*.tar.xz)
    tar --no-same-owner -xJf "${archives[0]}" -C "$stage"
fi
build="$stage/llvmnet-lbm"
mkdir -p "$build/native-$dataset" "$build/cil-$dataset"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
dotnet build "$root/tests/LlvmNet.Checks" -c Release --nologo
"$root/bin/llvmnet" -O1 -DSPEC -DSPEC_CPU -DSPEC_CPU_LP64 "${flags[@]}" \
    -I"$benchmark/src" "$benchmark/src/lbm.c" "$benchmark/src/main.c" -lm --keep-ir "$build/lbm.bc" -o "$build/lbm.dll"
clang-22 -O1 "$build/lbm.bc" -lm -o "$build/lbm-native"
for runtime in native cil; do
    run="$build/$runtime-$dataset"
    if [[ -d "$benchmark/data/all/input" ]]; then cp -a "$benchmark/data/all/input/." "$run/"; fi
    cp -a "$benchmark/data/$dataset/input/." "$run/"
    command=("$build/lbm-native")
    if [[ "$runtime" == cil ]]; then command=(dotnet "$build/lbm.dll"); fi
    (
        cd "$run"
        read -r -a arguments < lbm.in
        "${command[@]}" "${arguments[@]}" >lbm.out 2>lbm.err
    )
done
for expected in "$benchmark/data/$dataset/output/"*.out; do
    file="$(basename "$expected")"
    dotnet "$root/tests/LlvmNet.Checks/bin/Release/net10.0/LlvmNet.Checks.dll" compare-numeric "$expected" "$build/native-$dataset/$file" 0.0000001
    diff -u "$build/native-$dataset/$file" "$build/cil-$dataset/$file"
done
printf 'PASS: CPU %s %s.lbm_r %s, one process; native/CIL output matches and supplied reference meets declared tolerance. Development test, not a SPEC score.\n' "$suite" "$number" "$dataset"