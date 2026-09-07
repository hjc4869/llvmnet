#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C
root="${SPEC_PROJECT_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
config="${SPEC_CONFIG_DIR:-$root/config/spec}"
suite="$(realpath "${1:?Usage: spec-matrix.sh installed-suite toolchain-prefix [size] [benchmark-glob]}")"
toolchain="$(realpath "${2:?Specify the installed toolchain prefix}")"
size="${3:-test}"
pattern="${4:-*}"
optimization="${SPEC_OPT_LEVEL:-3}"
vectorize="${SPEC_VECTORIZE:-0}"
limit="${SPEC_TIMEOUT:-1800}"
stack_kb="${SPEC_STACK_KB:-unlimited}"
runner="${SPEC_MATRIX_RUNNER:-$root/scripts/spec-harness.sh}"
case "$size" in test|train|ref) ;; *) printf 'Size must be test, train or ref.\n' >&2; exit 2 ;; esac
case "$optimization" in 0|1|2|3|s|z) ;; *) printf 'Invalid SPEC_OPT_LEVEL: %s\n' "$optimization" >&2; exit 2 ;; esac
case "$vectorize" in 0|1) ;; *) printf 'SPEC_VECTORIZE must be 0 or 1.\n' >&2; exit 2 ;; esac
if [[ ! "$limit" =~ ^[1-9][0-9]*$ ]]; then printf 'SPEC_TIMEOUT must be a positive number of seconds.\n' >&2; exit 2; fi
if [[ ! -x "$suite/bin/runcpu" ]]; then printf 'Install the SPEC kit first: %s\n' "$suite" >&2; exit 1; fi
benchmarks=()
for directory in "$suite"/benchspec/CPU/[0-9][0-9][0-9].*; do
    [[ -d "$directory" ]] || continue
    benchmark="${directory##*/}"
    if [[ "$benchmark" == $pattern ]]; then benchmarks+=("$benchmark"); fi
done
if [[ ${#benchmarks[@]} -eq 0 ]]; then printf 'No installed benchmarks match %s.\n' "$pattern" >&2; exit 2; fi
output="$(realpath -m "${SPEC_MATRIX_OUTPUT:-$root/artifacts/spec-matrix}")"
mkdir -p "$output"
snapshot="${SPEC_MATRIX_SNAPSHOT:-}"
if [[ "$snapshot" != "${BASH_SOURCE[0]}" ]]; then
    run="$(mktemp -d "$output/$(basename "$suite")-O${optimization}-v${vectorize}-${size}.XXXXXXXX")"
    cp "${BASH_SOURCE[0]}" "$run/matrix.sh"
    cp "$runner" "$run/runner.sh"
    cp "$config/llvmnet.cfg" "$config/llvmnet.xml" "$run/"
    export SPEC_PROJECT_ROOT="$root" SPEC_CONFIG_DIR="$run" SPEC_MATRIX_RUNNER="$run/runner.sh" SPEC_MATRIX_SNAPSHOT="$run/matrix.sh"
    exec bash "$SPEC_MATRIX_SNAPSHOT" "$suite" "$toolchain" "$size" "$pattern"
fi
run="$(dirname "$snapshot")"
printf 'suite\truntime\tbenchmark\tsize\toptimization\tvectorize\tresult\texit_code\tlog\n' > "$run/results.tsv"
{
    printf 'suite=%s\ntoolchain=%s\nrunner=%s\ntimeout=%s\ncxx_standard=%s\n' "$suite" "$toolchain" "$runner" "$limit" "${SPEC_CXX_STANDARD:-suite-default}"
    printf 'stack_kb=%s\n' "$stack_kb"
    printf 'matrix=%s\nconfig=%s\n' "$snapshot" "$config"
    printf 'revision=%s\n' "$(git -C "$root" rev-parse HEAD)"
    printf 'started=%s\n' "$(date -u +%FT%TZ)"
    for binary in llvmnet.dll LlvmNet.Runtime.dll libllvmnet-llvm.so; do
        if [[ -f "$toolchain/lib/llvmnet/$binary" ]]; then sha256sum "$toolchain/lib/llvmnet/$binary"; fi
    done
    git -C "$root" status --short
} > "$run/environment.txt"
printf 'Checking %s benchmarks, both ABIs, -O%s, vectorize=%s, %s inputs. Results: %s\n' "${#benchmarks[@]}" "$optimization" "$vectorize" "$size" "$run/results.tsv"
failures=0
for benchmark in "${benchmarks[@]}"; do
    for runtime in system portable; do
        log="$run/$benchmark-$runtime.log"
        exit_code=0
        SPEC_OPT_LEVEL="$optimization" SPEC_VECTORIZE="$vectorize" SPEC_STACK_KB="$stack_kb" SPEC_REBUILD=1 \
            timeout --signal=TERM --kill-after=10 "$limit" bash "$runner" "$suite" "$toolchain" "$runtime" "$benchmark" "$size" > "$log" 2>&1 || exit_code=$?
        build_directory="$(sed -n -E 's/.*\((build_base_[A-Za-z0-9_.-]+)\).*/\1/p' "$log" | head -n 1)"
        if [[ -n "$build_directory" ]]; then
            mkdir -p "$run/$benchmark-$runtime-build"
            for build_log in "$suite/benchspec/CPU/$benchmark/build/$build_directory"/make*.out; do
                if [[ -f "$build_log" ]]; then cp "$build_log" "$run/$benchmark-$runtime-build/"; fi
            done
        fi
        result=FAIL
        if [[ "$exit_code" -eq 124 ]]; then
            result=TIMEOUT
        elif [[ "$exit_code" -eq 0 ]] && grep -Eq "^Success: (1x)?${benchmark//./\\.}([[:space:]]|$)" "$log"; then
            result=PASS
        fi
        if [[ "$result" != PASS ]]; then failures=$((failures + 1)); fi
        printf '%s\t%s\t%s\t%s\t-O%s\t%s\t%s\t%s\t%s\n' "$(basename "$suite")" "$runtime" "$benchmark" "$size" "$optimization" "$vectorize" "$result" "$exit_code" "$log" >> "$run/results.tsv"
        printf '%-7s %-22s %-8s exit=%s\n' "$result" "$benchmark" "$runtime" "$exit_code"
    done
done
printf 'completed=%s\nchecks=%s\nfailures=%s\n' "$(date -u +%FT%TZ)" "$(( ${#benchmarks[@]} * 2 ))" "$failures" > "$run/completion.txt"
printf 'Completed %s checks; %s did not pass. Development validation only, not a SPEC score.\n' "$(( ${#benchmarks[@]} * 2 ))" "$failures"
[[ "$failures" -eq 0 ]]