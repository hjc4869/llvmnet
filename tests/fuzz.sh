#!/usr/bin/env bash
# tags: fuzz
# requires: fuzz-build
# timeout: 7200
# Differential fuzzing with csmith and yarpgen (FD-16). Each seed makes one random C program per generator,
# compiled natively with clang-22 and by llvmnet in system and portable modes with the same optimization
# level; the outputs (checksums of the program state) are compared. A program llvmnet rejects with a
# diagnostic is recorded, not failed; a mismatch, crash, hang or internal error fails the run and keeps
# the program with a repro.sh.
#   FUZZ_SEEDS=200 FUZZ_FIRST_SEED=1 tests/fuzz.sh      seeds FIRST..FIRST+SEEDS-1 (defaults 100 and 1)
#   FUZZ_GENERATORS=csmith tests/fuzz.sh                 generators (default: csmith yarpgen)
#   FUZZ_OPT=-O2 tests/fuzz.sh                           one level for every seed (default: -O0..-O3 by seed)
#   FUZZ_RUNTIMES=portable tests/fuzz.sh                 modes to compare (default: system portable)
# Results: artifacts/tests/fuzz/{results.tsv,rejected.txt,failures/<generator>-<seed>/}.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output/work" "$test_output/failures"
export FUZZ_GENERATORS="${FUZZ_GENERATORS:-csmith yarpgen}"
# shellcheck disable=SC2086 # the generator list is meant to split
bash "$root/scripts/build-fuzzers.sh" $FUZZ_GENERATORS >/dev/null
export tools="$root/artifacts/tools" llvmnet test_output
export FUZZ_OPT="${FUZZ_OPT:-}" FUZZ_RUNTIMES="${FUZZ_RUNTIMES:-system portable}"
first="${FUZZ_FIRST_SEED:-1}" count="${FUZZ_SEEDS:-100}"

# One program. Prints a results.tsv row: generator, seed, opt, then per runtime one of match,
# "rejected: <diagnostic>", mismatch, crash, timeout or internal; or "skipped" with a reason when the
# native program does not finish cleanly.
fuzz_one() {
    local generator="$1" seed="$2" work="$test_output/work/$1-$2" levels=(-O0 -O1 -O2 -O3)
    local flags native_flags=() sources opt runtime status row verdict
    opt="${FUZZ_OPT:-${levels[seed % 4]}}"
    mkdir -p "$work"
    case "$generator" in
        csmith)
            "$tools/csmith/bin/csmith" --seed "$seed" --output "$work/program.c" >/dev/null 2>&1 || status=$?
            flags=("$opt" -w -I"$tools/csmith/include") sources=(program.c) ;;
        yarpgen)
            "$tools/yarpgen/bin/yarpgen" --std=c --seed="$seed" --out-dir="$work" >/dev/null 2>&1 || status=$?
            # Its arrays can exceed 2 GB, beyond the native default code model.
            flags=("$opt" -w) sources=(driver.c func.c) native_flags=(-mcmodel=large) ;;
    esac
    if [[ "${status:-0}" -ne 0 ]]; then
        printf '%s\t%s\t%s\tskipped\tgenerator failed\n' "$generator" "$seed" "$opt"
        rm -rf "$work"
        return
    fi
    cd "$work"
    if ! clang-22 "${flags[@]}" "${native_flags[@]}" "${sources[@]}" -o native >native.log 2>&1 ||
        ! timeout 10 ./native >native.txt 2>&1; then
        # Generated programs may loop for a long time; such seeds say nothing about llvmnet.
        printf '%s\t%s\t%s\tskipped\tnative did not finish\n' "$generator" "$seed" "$opt"
        rm -rf "$work"
        return
    fi
    row="$generator"$'\t'"$seed"$'\t'"$opt" verdict=pass
    for runtime in $FUZZ_RUNTIMES; do
        status=0
        "$llvmnet" --runtime="$runtime" "${flags[@]}" "${sources[@]}" -o "program-$runtime.dll" \
            >"program-$runtime.log" 2>&1 || status=$?
        if [[ "$status" -eq 1 ]]; then
            row+=$'\t'"rejected: $(grep -m1 'error:' "program-$runtime.log" | sed 's/^.*error: //')"
            continue
        elif [[ "$status" -ne 0 ]]; then
            row+=$'\t'"internal (exit $status)" verdict=fail
            continue
        fi
        status=0
        timeout 300 dotnet "program-$runtime.dll" >"program-$runtime.txt" 2>&1 || status=$?
        if [[ "$status" -eq 124 ]]; then
            row+=$'\t'timeout verdict=fail
        elif [[ "$status" -ne 0 ]]; then
            row+=$'\t'"crash (exit $status)" verdict=fail
        elif ! cmp -s native.txt "program-$runtime.txt"; then
            row+=$'\t'mismatch verdict=fail
        else
            row+=$'\t'match
        fi
    done
    printf '%s\n' "$row"
    cd "$test_output"
    if [[ "$verdict" == fail ]]; then
        {
            printf '#!/usr/bin/env bash\n# Rebuild %s seed %s natively and with llvmnet and compare the outputs.\n' \
                "$generator" "$seed"
            printf 'set -eu\ncd "$(dirname "$0")"\n'
            printf 'clang-22 %s %s %s -o native && ./native >native.txt\n' "${flags[*]}" "${native_flags[*]}" "${sources[*]}"
            for runtime in $FUZZ_RUNTIMES; do
                printf '%q --runtime=%s %s %s -o program-%s.dll && dotnet program-%s.dll | diff -u native.txt -\n' \
                    "$llvmnet" "$runtime" "${flags[*]}" "${sources[*]}" "$runtime" "$runtime"
            done
        } >"$work/repro.sh"
        mv "$work" "$test_output/failures/$generator-$seed"
    else
        rm -rf "$work"
    fi
}
export -f fuzz_one

for seed in $(seq "$first" $((first + count - 1))); do
    for generator in $FUZZ_GENERATORS; do printf '%s %s\n' "$generator" "$seed"; done
done | xargs -P "${JOBS:-$(($(nproc) / 2))}" -L1 bash -c 'fuzz_one "$1" "$2"' _ |
    sort -k1,1 -k2,2n >"$test_output/results.tsv"
rmdir "$test_output/work" 2>/dev/null || true

rejected_programs="$(awk -F '\t' '{ for (i = 4; i <= NF; i++) if ($i ~ /^rejected: /) { n++; break } } END { print n + 0 }' \
    "$test_output/results.tsv")"
cut -f4- "$test_output/results.tsv" | tr '\t' '\n' | grep '^rejected: ' | sort | uniq -c | sort -rn \
    >"$test_output/rejected.txt" || true
failures=()
for failure in "$test_output"/failures/*; do [[ -d "$failure" ]] && failures+=("$(basename "$failure")"); done
for generator in $FUZZ_GENERATORS; do
    awk -F '\t' -v g="$generator" -v first="$first" -v last=$((first + count - 1)) '
        $1 == g { total++; if ($4 == "skipped") { skipped++; next }
                  ok = 1; for (i = 4; i <= NF; i++) if ($i != "match") ok = 0; matched += ok }
        END { printf "%s seeds %d..%d: %d programs, %d skipped (native did not finish), %d matched in every mode\n",
                     g, first, last, total, skipped, matched }' "$test_output/results.tsv"
done
printf '%d programs had a build rejected with a diagnostic (%s)\n' "$rejected_programs" "$test_output/rejected.txt"
if [[ ${#failures[@]} -gt 0 ]]; then
    printf 'FAIL: %d programs differ from native: %s\n' "${#failures[@]}" "${failures[*]}" >&2
    for failure in "${failures[@]}"; do
        awk -F '\t' -v key="$failure" '$1 "-" $2 == key' "$test_output/results.tsv" >&2
    done
    printf 'Programs and repro.sh scripts are in %s\n' "$test_output/failures" >&2
    exit 1
fi
printf 'PASS: no program differs from native\n'
