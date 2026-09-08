#!/usr/bin/env bash
set -euo pipefail
root="${SPEC_PROJECT_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
baseline="$(realpath "${1:?Usage: spec-portable-check.sh baseline.tsv toolchain-prefix [benchmark-glob]}")"
toolchain="$(realpath "${2:?Specify the frozen toolchain prefix}")"
pattern="${3:-*}"
limit="${SPEC_TIMEOUT:-900}"
if [[ ! "$limit" =~ ^[1-9][0-9]*$ ]]; then exit 2; fi
if [[ "${SPEC_PORTABLE_SNAPSHOT:-}" != "${BASH_SOURCE[0]}" ]]; then
    output="$(realpath -m "${SPEC_PORTABLE_OUTPUT:-$root/artifacts/spec-portable-audit}")"
    mkdir -p "$output"
    run="$(mktemp -d "$output/retry.XXXXXXXX")"
    cp "${BASH_SOURCE[0]}" "$run/check.sh"
    cp "$root/scripts/spec-harness.sh" "$run/runner.sh"
    cp "$root/config/spec/llvmnet.cfg" "$root/config/spec/llvmnet.xml" "$run/"
    cp "$baseline" "$run/baseline.tsv"
    export SPEC_PROJECT_ROOT="$root" SPEC_CONFIG_DIR="$run" SPEC_PORTABLE_SNAPSHOT="$run/check.sh"
    exec bash "$run/check.sh" "$run/baseline.tsv" "$toolchain" "$pattern"
fi
run="$(dirname "$SPEC_PORTABLE_SNAPSHOT")"
{
    printf 'started=%s\ntoolchain=%s\npattern=%s\nsize=test\noptimization=3\nvectorize=0\ntimeout=%s\n' "$(date -u +%FT%TZ)" "$toolchain" "$pattern" "$limit"
    sha256sum "$toolchain/lib/llvmnet/llvmnet.dll" "$toolchain/lib/llvmnet/LlvmNet.Runtime.dll" \
        "$toolchain/lib/llvmnet/libllvmnet-llvm.so" "$toolchain/sysroot/dotnet64-v1/lib/libcxx-managed.a"
    find "$toolchain/sysroot/dotnet64-v1" -type f -print0 | sort -z | xargs -0 sha256sum
} > "$run/environment.txt"
printf 'suite\truntime\tbenchmark\tsize\toptimization\tvectorize\tresult\texit_code\tlog\tcopies\tthreads\tthreading_profile\n' > "$run/results.tsv"
checked=0
failures=0
while IFS=$'\t' read -r benchmark previous remainder; do
    [[ "$benchmark" != benchmark && "$previous" != PASS && "$benchmark" == $pattern ]] || continue
    if [[ ! "$benchmark" =~ ^[0-9]{3}\.[A-Za-z0-9_-]+$ ]]; then exit 2; fi
    suite="$root/artifacts/spec2026-harness"
    if [[ "$benchmark" == [56]* && "$benchmark" != 998.* && "$benchmark" != 999.* ]]; then suite="$root/artifacts/spec2017-harness"; fi
    threads=1
    if [[ "$benchmark" == *_s ]]; then threads=4; fi
    profile=serial
    if [[ "$threads" -gt 1 ]]; then profile=kit-openmp-v2; fi
    log="$run/$benchmark-portable.log"
    exit_code=0
    SPEC_THREADS="$threads" SPEC_OPT_LEVEL=3 SPEC_VECTORIZE=0 SPEC_REBUILD=1 SPEC_REUSE_BUILD=0 \
        timeout --signal=TERM --kill-after=10 "$limit" bash "$run/runner.sh" "$suite" "$toolchain" portable "$benchmark" test > "$log" 2>&1 || exit_code=$?
    build_directory="$(sed -n -E 's/.*\((build_base_[A-Za-z0-9_.-]+)\).*/\1/p' "$log" | head -n 1)"
    if [[ -n "$build_directory" ]]; then
        mkdir -p "$run/$benchmark-portable-build"
        for make_log in "$suite/benchspec/CPU/$benchmark/build/$build_directory"/make*.out; do
            if [[ -f "$make_log" ]]; then cp "$make_log" "$run/$benchmark-portable-build/"; fi
        done
    fi
    result=FAIL
    if [[ "$exit_code" -eq 124 ]]; then result=TIMEOUT
    elif [[ "$exit_code" -eq 0 ]] && grep -Eq "^Success: (1x)?${benchmark//./\\.}([[:space:]]|$)" "$log"; then result=PASS; fi
    checked=$((checked + 1))
    if [[ "$result" != PASS ]]; then failures=$((failures + 1)); fi
    printf '%s\tportable\t%s\ttest\t-O3\t0\t%s\t%s\t%s\t1\t%s\t%s\n' "$(basename "$suite")" "$benchmark" "$result" "$exit_code" "$log" "$threads" "$profile" >> "$run/results.tsv"
    printf '%s %s (exit=%s)\n' "$benchmark" "$result" "$exit_code"
done < "$baseline"
if [[ "$checked" -eq 0 ]]; then printf 'No failed baseline entries matched.\n' >&2; exit 2; fi
printf 'completed=%s\nchecked=%s\nfailures=%s\n' "$(date -u +%FT%TZ)" "$checked" "$failures" > "$run/completion.txt"
printf 'Results: %s\n' "$run/results.tsv"