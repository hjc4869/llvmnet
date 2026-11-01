#!/usr/bin/env bash
# tags: fuzz
# requires: fuzz-build
# timeout: 7200
# Differential fuzzing with csmith and yarpgen. Each seed makes one random C program per generator,
# compiled natively with clang-22 and by llvmnet in system and portable modes with the same optimization
# level; the outputs (checksums of the program state) are compared. A program llvmnet rejects with a
# diagnostic is recorded, not failed; a mismatch, crash, hang or internal error fails the run and keeps
# the program with a repro.sh.
# Every program runs with a time limit and a resident memory limit: natively 10 s and FUZZ_MEMORY_MB, under
# llvmnet 300 s and 512 MB more for the .NET runtime. A native program over either limit is skipped with the
# reason (one YARPGen seed in five has arrays of several GB, up to hundreds); an llvmnet build over its limit
# fails like a hang. With the defaults the run stays under about JOBS x 3 GB: 6 GB with the two jobs of the
# 4-core, 16 GB nightly runner.
#   FUZZ_SEEDS=200 FUZZ_FIRST_SEED=1 tests/fuzz.sh      seeds FIRST..FIRST+SEEDS-1 (defaults 100 and 1)
#   FUZZ_GENERATORS=csmith tests/fuzz.sh                 generators (default: csmith yarpgen)
#   FUZZ_OPT=-O2 tests/fuzz.sh                           one level for every seed (default: -O0..-O3 by seed)
#   FUZZ_RUNTIMES=portable tests/fuzz.sh                 modes to compare (default: system portable)
#   FUZZ_MEMORY_MB=4096 tests/fuzz.sh                    memory limit of a native program (default: 2048)
#   JOBS=8 tests/fuzz.sh                                 programs in parallel (default: nproc/2, at most 4)
# Results: artifacts/tests/fuzz/{results.tsv,rejected.txt,failures/<generator>-<seed>/}.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output/work" "$test_output/failures"
export FUZZ_GENERATORS="${FUZZ_GENERATORS:-csmith yarpgen}"
# shellcheck disable=SC2086 # the generator list is meant to split
bash "$root/scripts/build-fuzzers.sh" $FUZZ_GENERATORS >/dev/null
export tools="$root/artifacts/tools" llvmnet test_output
export FUZZ_OPT="${FUZZ_OPT:-}" FUZZ_RUNTIMES="${FUZZ_RUNTIMES:-system portable}"
export FUZZ_MEMORY_MB="${FUZZ_MEMORY_MB:-2048}" page_size
page_size="$(getconf PAGESIZE)"
first="${FUZZ_FIRST_SEED:-1}" count="${FUZZ_SEEDS:-100}" half=$(($(nproc) / 2))
jobs="${JOBS:-$((half < 1 ? 1 : half > 4 ? 4 : half))}"

# Runs a program with a time limit in seconds and a resident memory limit in MB, checking its resident set
# every 20 ms; an address-space limit (ulimit -v) would not do, because the .NET runtime reserves gigabytes
# that it never touches, and a cgroup is not available to an unprivileged user or in CI's container. Returns
# the program's exit status and sets limit_exceeded to "time" or "memory" when it was stopped at a limit.
run_limited() {
    local seconds="$1" pages=$(($2 * 1048576 / page_size)) note="$PWD/limit-exceeded" pid watchdog status=0
    shift 2
    rm -f "$note"
    "$@" &
    pid=$!
    (
        # A read with a timeout on a pipe that nobody writes to sleeps without starting a process.
        exec {sleep_fd}<> <(:)
        end=$((SECONDS + seconds + 1))
        while read -r _ resident _ 2>/dev/null <"/proc/$pid/statm"; do
            if ((resident > pages)); then echo memory >"$note"; kill -KILL "$pid"; exit; fi
            if ((SECONDS >= end)); then echo time >"$note"; kill -KILL "$pid"; exit; fi
            read -r -t 0.02 -u "$sleep_fd" || true
        done
    ) </dev/null >/dev/null 2>&1 &
    watchdog=$!
    wait "$pid" || status=$?
    kill "$watchdog" 2>/dev/null || true
    wait "$watchdog" 2>/dev/null || true
    limit_exceeded=""
    [[ -f "$note" ]] && limit_exceeded="$(<"$note")" && rm -f "$note"
    return "$status"
}
export -f run_limited

# One program. Prints a results.tsv row: generator, seed, opt, then per runtime one of match,
# "rejected: <diagnostic>", mismatch, crash, timeout, memory or internal; or "skipped" with a reason when
# the native program does not build, fails, runs out of time or exceeds the memory limit.
fuzz_one() {
    local generator="$1" seed="$2" work="$test_output/work/$1-$2" levels=(-O0 -O1 -O2 -O3)
    local flags native_flags=() sources opt runtime status row verdict reason="" static limit_exceeded
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
    # Generated programs may loop for a long time or need more memory than the host has; such seeds say
    # nothing about llvmnet. Static data over the limit is caught before the program runs, because a host
    # that cannot map it fails the program's start (exit 139) instead.
    if ! clang-22 "${flags[@]}" "${native_flags[@]}" "${sources[@]}" -o native >native.log 2>&1; then
        reason="native did not build"
    elif static="$(llvm-size-22 native | awk 'NR == 2 { print int(($2 + $3) / 1048576) }')" &&
        ((static > FUZZ_MEMORY_MB)); then
        reason="over the memory limit: $static MB of static data"
    else
        status=0
        run_limited 10 "$FUZZ_MEMORY_MB" ./native >native.txt 2>&1 || status=$?
        case "$limit_exceeded" in
            memory) reason="over the memory limit: more than $FUZZ_MEMORY_MB MB resident" ;;
            time) reason="native did not finish in 10 s" ;;
            *) [[ "$status" -eq 0 ]] || reason="native failed (exit $status)" ;;
        esac
    fi
    if [[ -n "$reason" ]]; then
        printf '%s\t%s\t%s\tskipped\t%s\n' "$generator" "$seed" "$opt" "$reason"
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
        run_limited 300 $((FUZZ_MEMORY_MB + 512)) dotnet "program-$runtime.dll" >"program-$runtime.txt" 2>&1 ||
            status=$?
        if [[ "$limit_exceeded" == time ]]; then
            row+=$'\t'timeout verdict=fail
        elif [[ "$limit_exceeded" == memory ]]; then
            # The native program stayed under the limit, and the runtime needs about 50 MB of the 512 more.
            row+=$'\t'"memory (over $((FUZZ_MEMORY_MB + 512)) MB)" verdict=fail
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
done | xargs -P "$jobs" -L1 bash -c 'fuzz_one "$1" "$2"' _ |
    sort -k1,1 -k2,2n >"$test_output/results.tsv"
rmdir "$test_output/work" 2>/dev/null || true

rejected_programs="$(awk -F '\t' '{ for (i = 4; i <= NF; i++) if ($i ~ /^rejected: /) { n++; break } } END { print n + 0 }' \
    "$test_output/results.tsv")"
cut -f4- "$test_output/results.tsv" | tr '\t' '\n' | grep '^rejected: ' | sort | uniq -c | sort -rn \
    >"$test_output/rejected.txt" || true
failures=()
for failure in "$test_output"/failures/*; do [[ -d "$failure" ]] && failures+=("$(basename "$failure")"); done
for generator in $FUZZ_GENERATORS; do
    awk -F '\t' -v g="$generator" -v first="$first" -v last=$((first + count - 1)) -v limit="$FUZZ_MEMORY_MB" '
        $1 == g { total++; if ($4 == "skipped") { skipped++; memory += ($5 ~ /^over the memory limit/); next }
                  ok = 1; for (i = 4; i <= NF; i++) if ($i != "match") ok = 0; matched += ok }
        END { printf "%s seeds %d..%d: %d programs, %d skipped (%d over the %d MB memory limit), %d matched in every mode\n",
                     g, first, last, total, skipped, memory, limit, matched }' "$test_output/results.tsv"
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
