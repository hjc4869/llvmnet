#!/usr/bin/env bash
# tags: core
# The benchmark harness (PF-01, scripts/benchmark.py): the microbenchmarks build natively and in portable mode and
# their outputs match native under the JIT, and the regression thresholds pass an unchanged run, fail a slower
# benchmark or a slower geometric mean, and ignore a host that is uniformly faster or slower. The nightly CI job runs
# the measured harness; this test keeps the harness and its programs working on every pull request.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
python3 "$root/scripts/benchmark.py" --validate-only --group micro --group startup --engines native,jit \
    --output "$test_output/validate" >"$test_output/validate.log" 2>&1 || { cat "$test_output/validate.log" >&2; exit 1; }
passed="$(grep -c '^PASS: ' "$test_output/validate.log")"
printf 'PASS: %d microbenchmarks build and match native output under the JIT\n' "$passed"
# The simd group (SV-20) at its three ISA levels, which only the vectorized engines build.
python3 "$root/scripts/benchmark.py" --validate-only --group simd --only 'simd-sad8*' --engines native,native-vec,jit-vec \
    --output "$test_output/validate-simd" >"$test_output/validate-simd.log" 2>&1 || { cat "$test_output/validate-simd.log" >&2; exit 1; }
test "$(grep -c '^PASS: simd-sad8.* output matches native (native-vec, jit-vec)$' "$test_output/validate-simd.log")" -eq 3
if [[ -e "$test_output/validate-simd/build/simd-sad8/simd-sad8.dll" ]]; then printf 'FAIL: the simd group built a scalar llvmnet engine\n' >&2; exit 1; fi
printf 'PASS: the SIMD kernels build and match native at the baseline, x86-64-v2 and x86-64-v3 vectorized\n'

# Threshold checks on synthetic results: a baseline of three benchmarks, then runs that differ from it.
cat >"$test_output/baseline.tsv" <<'EOF'
benchmark	engine	median_s
a	native	1.0000
a	jit	2.0000
b	native	0.5000
b	jit	1.5000
c	native	2.0000
c	jit	3.0000
EOF
# results <a jit> <b jit> <c jit> <host factor>: native times are the baseline's scaled by the host factor; each run's
# fastest time, which the thresholds judge, equals its median here.
results() {
    printf 'benchmark\tgroup\tengine\truns\tmedian_s\tmin_s\tmax_s\tuser_s\tsystem_s\tmax_rss_kib\tratio_to_native\n'
    local name native jit
    for entry in "a 1.0 $1" "b 0.5 $2" "c 2.0 $3"; do
        read -r name native jit <<<"$entry"
        native="$(awk -v n="$native" -v f="$4" 'BEGIN { print n * f }')"
        jit="$(awk -v j="$jit" -v f="$4" 'BEGIN { print j * f }')"
        printf '%s\tsuite\tnative\t5\t%s\t%s\t0\t0\t0\t0\t1\n%s\tsuite\tjit\t5\t%s\t%s\t0\t0\t0\t0\t0\n' "$name" "$native" "$native" "$name" "$jit" "$jit"
    done
}
check() {  # check <expected exit status> <description> <results arguments...>
    local expected="$1" description="$2"
    shift 2
    results "$@" >"$test_output/results.tsv"
    local status=0
    python3 "$root/scripts/benchmark.py" --compare "$test_output/results.tsv" --baseline "$test_output/baseline.tsv" \
        >"$test_output/compare.log" 2>&1 || status=$?
    if [[ "$status" != "$expected" ]]; then
        printf 'FAIL: %s: exit status %s, expected %s\n' "$description" "$status" "$expected" >&2
        cat "$test_output/compare.log" >&2
        exit 1
    fi
}
check 0 "unchanged" 2.0 1.5 3.0 1
check 0 "within the tolerances" 2.3 1.6 3.1 1
check 0 "a host twice as slow" 2.0 1.5 3.0 2
check 0 "a host three times as fast" 2.0 1.5 3.0 0.3333
check 1 "one benchmark 40% slower" 2.8 1.5 3.0 1
check 1 "one benchmark 40% slower on a host twice as slow" 2.8 1.5 3.0 2
check 1 "every benchmark 15% slower (geometric mean)" 2.3 1.725 3.45 1
grep -q '^REGRESSION: jit geometric mean' "$test_output/compare.log"
printf 'PASS: the thresholds ignore host speed and fail a 40%% slower benchmark and a 15%% slower geometric mean\n'
