#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C
root="${SPEC_PROJECT_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
suite2017="$(realpath "${1:?Usage: spec-ordered.sh cpu2017-suite cpu2026-suite toolchain-prefix [size]}")"
suite2026="$(realpath "${2:?Specify the installed CPU 2026 suite}")"
toolchain="$(realpath "${3:?Specify the installed toolchain prefix}")"
size="${4:-test}"
required="${SPEC_REQUIRED_ABIS:-system}"
start="${SPEC_ORDER_START:-}"
config="${SPEC_CONFIG_DIR:-$root/config/spec}"
matrix="${SPEC_ORDER_MATRIX:-$root/scripts/spec-matrix.sh}"
runner="${SPEC_MATRIX_RUNNER:-$root/scripts/spec-harness.sh}"
case "$size" in test|train|ref) ;; *) printf 'Size must be test, train or ref.\n' >&2; exit 2 ;; esac
case "$required" in system|both) ;; *) printf 'SPEC_REQUIRED_ABIS must be system or both.\n' >&2; exit 2 ;; esac
groups=(2017-intrate 2026-intrate 2026-fprate 2026-intspeed 2026-fpspeed 2017-fprate 2017-intspeed 2017-fpspeed)
for suite in "$suite2017" "$suite2026"; do
    if [[ ! -x "$suite/bin/runcpu" || ! -x "$suite/bin/specperl" ]]; then
        printf 'Install the SPEC kit first: %s\n' "$suite" >&2
        exit 2
    fi
done
snapshot="${SPEC_ORDER_SNAPSHOT:-}"
if [[ "$snapshot" != "${BASH_SOURCE[0]}" ]]; then
    output="$(realpath -m "${SPEC_ORDER_OUTPUT:-$root/artifacts/spec-ordered}")"
    mkdir -p "$output"
    run="$(mktemp -d "$output/ordered-${size}.XXXXXXXX")"
    cp "${BASH_SOURCE[0]}" "$run/ordered.sh"
    cp "$matrix" "$run/matrix-source.sh"
    cp "$runner" "$run/harness-source.sh"
    cp "$config/llvmnet.cfg" "$config/llvmnet.xml" "$run/"
    for group in "${groups[@]}"; do
        suite="$suite2017"
        if [[ "$group" == 2026-* ]]; then suite="$suite2026"; fi
        cp "$suite/benchspec/CPU/${group#*-}.bset" "$run/$group.bset"
    done
    export SPEC_PROJECT_ROOT="$root" SPEC_CONFIG_DIR="$run" SPEC_ORDER_MATRIX="$run/matrix-source.sh"
    export SPEC_MATRIX_RUNNER="$run/harness-source.sh" SPEC_ORDER_SNAPSHOT="$run/ordered.sh"
    exec bash "$SPEC_ORDER_SNAPSHOT" "$suite2017" "$suite2026" "$toolchain" "$size"
fi
run="$(dirname "$snapshot")"
printf 'group\tbenchmark\tsystem\tportable\tresults\n' > "$run/progress.tsv"
printf 'started=%s\nrequired_abis=%s\nstart=%s\nsize=%s\ncopies=1\nrate_threads=1\nspeed_threads=4\n' \
    "$(date -u +%FT%TZ)" "$required" "$start" "$size" > "$run/environment.txt"
printf 'Progress: %s\n' "$run/progress.tsv"
checked=0
portable_failures=0
for group in "${groups[@]}"; do
    suite="$suite2017"
    if [[ "$group" == 2026-* ]]; then suite="$suite2026"; fi
    "$suite/bin/specperl" -e '
        my $path = shift;
        open my $input, "<", $path or die "$path: $!";
        local $/;
        my $data = <$input>;
        close $input;
        my @entries;
        if ($data =~ /^\s*\{/) {
            require JSON::PP;
            @entries = @{JSON::PP::decode_json($data)->{benchmarks}};
        } else {
            our @benchmarks;
            my $loaded = do $path;
            die "$path: " . ($@ || $! || "invalid benchmark set") unless $loaded;
            @entries = @benchmarks;
        }
        die "Empty benchmark set: $path" unless @entries;
        print "$_\n" for sort @entries;
    ' "$run/$group.bset" > "$run/$group-benchmarks.txt"
    mapfile -t benchmarks < "$run/$group-benchmarks.txt"
    threads=1
    if [[ "$group" == *speed ]]; then threads=4; fi
    for benchmark in "${benchmarks[@]}"; do
        if [[ ! "$benchmark" =~ ^[0-9]{3}\.[A-Za-z0-9_-]+$ ]]; then
            printf 'Invalid benchmark entry: %s\n' "$benchmark" >&2
            exit 2
        fi
        if [[ -n "$start" ]]; then
            if [[ "$group:$benchmark" != "$start" ]]; then continue; fi
            start=""
        fi
        printf '%s %s: copies=1 threads=%s\n' "$group" "$benchmark" "$threads"
        log="$run/$group-$benchmark.log"
        exit_code=0
        SPEC_THREADS="$threads" SPEC_MATRIX_OUTPUT="$run/$group" \
            bash "$matrix" "$suite" "$toolchain" "$size" "$benchmark" > "$log" 2>&1 || exit_code=$?
        results="$(sed -n 's/^Checking .* Results: //p' "$log")"
        if [[ "$exit_code" -gt 1 || -z "$results" || ! -f "$results" || ! -f "$(dirname "$results")/completion.txt" ]]; then
            printf 'result=BLOCKED\nbenchmark=%s:%s\nreason=incomplete-matrix\n' "$group" "$benchmark" > "$run/completion.txt"
            printf 'Stopped: incomplete matrix for %s. Log: %s\n' "$benchmark" "$log" >&2
            exit 1
        fi
        system_result="$(awk -F '\t' '$2 == "system" { print $7 }' "$results")"
        portable_result="$(awk -F '\t' '$2 == "portable" { print $7 }' "$results")"
        printf '%s\t%s\t%s\t%s\t%s\n' "$group" "$benchmark" "${system_result:-MISSING}" "${portable_result:-MISSING}" "$results" >> "$run/progress.tsv"
        checked=$((checked + 1))
        if [[ "$portable_result" != PASS ]]; then portable_failures=$((portable_failures + 1)); fi
        printf '  system=%s portable=%s\n' "${system_result:-MISSING}" "${portable_result:-MISSING}"
        if [[ "$system_result" != PASS || "$required" == both && "$portable_result" != PASS ]]; then
            printf 'result=BLOCKED\nbenchmark=%s:%s\nchecked=%s\nrequired_abis=%s\n' "$group" "$benchmark" "$checked" "$required" > "$run/completion.txt"
            printf 'Stopped before the next benchmark; required ABI validation failed.\n' >&2
            exit 1
        fi
    done
done
if [[ -n "$start" ]]; then printf 'SPEC_ORDER_START was not found: %s\n' "$start" >&2; exit 2; fi
printf 'result=COMPLETE\ncompleted=%s\nchecked=%s\nportable_failures=%s\nrequired_abis=%s\n' \
    "$(date -u +%FT%TZ)" "$checked" "$portable_failures" "$required" > "$run/completion.txt"
printf 'Completed %s benchmark gates; %s portable cases did not pass. Development validation only.\n' "$checked" "$portable_failures"