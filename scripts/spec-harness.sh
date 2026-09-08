#!/usr/bin/env bash
set -euo pipefail
root="${SPEC_PROJECT_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
config="${SPEC_CONFIG_DIR:-$root/config/spec}"
suite="${1:?Usage: spec-harness.sh installed-suite toolchain-prefix [runtime] [benchmark] [size]}"
toolchain="${2:?Specify the installed toolchain prefix}"
runtime="${3:-system}"
benchmark="${4:-505.mcf_r}"
size="${5:-test}"
optimization="${SPEC_OPT_LEVEL:-3}"
vectorize="${SPEC_VECTORIZE:-0}"
stack_kb="${SPEC_STACK_KB:-unlimited}"
case "$benchmark" in *_s|*.specrand_*s) default_threads=4 ;; *) default_threads=1 ;; esac
threads="${SPEC_THREADS:-$default_threads}"
case "$runtime" in system|portable) ;; *) printf 'Runtime must be system or portable.\n' >&2; exit 2 ;; esac
case "$optimization" in 0|1|2|3|s|z) ;; *) printf 'Invalid SPEC_OPT_LEVEL: %s\n' "$optimization" >&2; exit 2 ;; esac
case "$vectorize" in 0|1) ;; *) printf 'SPEC_VECTORIZE must be 0 or 1.\n' >&2; exit 2 ;; esac
if [[ ! "$threads" =~ ^[1-9][0-9]*$ ]]; then printf 'SPEC_THREADS must be a positive integer.\n' >&2; exit 2; fi
if [[ "$stack_kb" != unlimited && ! "$stack_kb" =~ ^[1-9][0-9]*$ ]]; then printf 'SPEC_STACK_KB must be unlimited or a positive KiB count.\n' >&2; exit 2; fi
ulimit -S -s "$stack_kb"
export OMP_NUM_THREADS="$threads" OMP_THREAD_LIMIT="$threads" OMP_DYNAMIC=FALSE
options=()
if [[ "${SPEC_REBUILD:-0}" == 1 ]]; then options+=(--rebuild); fi
case "${SPEC_REUSE_BUILD:-0}" in
    0) ;;
    1) options+=(--make_no_clobber) ;;
    *) printf 'SPEC_REUSE_BUILD must be 0 or 1.\n' >&2; exit 2 ;;
esac
if [[ "$runtime" == system && -n "${SPEC_SYSTEM_CXX:-}" ]]; then
    if [[ ! -f "$SPEC_SYSTEM_CXX" ]]; then printf 'Missing system C++ bitcode archive: %s\n' "$SPEC_SYSTEM_CXX" >&2; exit 2; fi
    options+=(--define "llvmnet_system_cxx=$(realpath "$SPEC_SYSTEM_CXX")")
fi
if [[ "$runtime" == system && "$threads" -gt 1 ]]; then
    openmp_prefix="${SPEC_OPENMP_PREFIX:-$root/artifacts/system-openmp}"
    openmp_library="${SPEC_OPENMP_LIBRARY:-libomp.so.5}"
    if [[ -f "$openmp_prefix/include/omp.h" && -f "$openmp_prefix/lib/libomp.so.5" ]]; then
        options+=(--define "llvmnet_openmp_include=$(realpath "$openmp_prefix/include")")
        openmp_library="${SPEC_OPENMP_LIBRARY:-$(realpath "$openmp_prefix/lib/libomp.so.5")}"
        export LD_LIBRARY_PATH="$(realpath "$openmp_prefix/lib")${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    fi
    options+=(--define "llvmnet_openmp_library=$openmp_library")
fi
suite="$(realpath "$suite")"
toolchain="$(realpath "$toolchain")"
if [[ ! -x "$suite/bin/runcpu" ]]; then printf 'Install the SPEC kit first: %s\n' "$suite" >&2; exit 1; fi
cxx_standard="${SPEC_CXX_STANDARD:-14}"
suite_version=2017
if [[ -f "$suite/benchspec/CPU/CPU.bset" ]]; then
    IFS= read -r header < "$suite/benchspec/CPU/CPU.bset"
    if [[ "$header" == '{' ]]; then
        metric="$("$suite/bin/specperl" -MJSON::PP -0777 -ne 'print decode_json($_)->{metric}' "$suite/benchspec/CPU/CPU.bset")"
        if [[ "$metric" == CPU2026 ]]; then
            suite_version=2026
            if [[ -z "${SPEC_CXX_STANDARD:-}" ]]; then cxx_standard=17; fi
        fi
    fi
fi
case "$cxx_standard" in 14|17|20|23) ;; *) printf 'Invalid SPEC_CXX_STANDARD: %s\n' "$cxx_standard" >&2; exit 2 ;; esac
if [[ ! -f "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" ]]; then
    printf 'Complete the full ffmpeg video validation before running SPEC.\n' >&2
    exit 1
fi
diff -q "$root/artifacts/ffmpeg-browser/video-validation/native-frames.txt" "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" >/dev/null
mkdir -p "$suite/config/flags"
cp "$config/llvmnet.cfg" "$suite/config/llvmnet.cfg"
cp "$config/llvmnet.xml" "$suite/config/flags/llvmnet.xml"
cd "$suite"
set +u
source ./shrc
set -u
runcpu --config=llvmnet --define "llvmnet_dir=$toolchain" --define "llvmnet_runtime=$runtime" \
    --define "llvmnet_opt_level=$optimization" --define "llvmnet_vectorize=$vectorize" \
    --define "llvmnet_cxx_standard=$cxx_standard" --define "llvmnet_threads=$threads" \
    --define "llvmnet_suite=$suite_version" \
    --size="$size" --iterations=1 --copies=1 --threads="$threads" --noreportable --tune=base --action=run "${options[@]}" "$benchmark"