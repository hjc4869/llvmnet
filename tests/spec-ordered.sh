#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$(mktemp -d "$root/artifacts/tests/spec-ordered.XXXXXXXX")"
suite2017="$output/spec2017"
suite2026="$output/spec2026"
mkdir -p "$output/toolchain"
for suite in "$suite2017" "$suite2026"; do
    mkdir -p "$suite/bin" "$suite/benchspec/CPU"
    touch "$suite/bin/runcpu"
    chmod +x "$suite/bin/runcpu"
    ln -s "$(command -v perl)" "$suite/bin/specperl"
done
add_group() {
    local suite="$1" group="$2" lower="$3" higher="$4"
    mkdir -p "$suite/benchspec/CPU/$lower" "$suite/benchspec/CPU/$higher"
    if [[ "$suite" == "$suite2017" ]]; then
        printf '@benchmarks = qw(%s %s);\n1;\n' "$higher" "$lower" > "$suite/benchspec/CPU/$group.bset"
    else
        printf '{"benchmarks":["%s","%s"]}\n' "$higher" "$lower" > "$suite/benchspec/CPU/$group.bset"
    fi
}
add_group "$suite2017" intrate 100.low_r 101.high_r
add_group "$suite2026" intrate 200.low_r 201.high_r
add_group "$suite2026" fprate 300.low_r 301.high_r
add_group "$suite2026" intspeed 400.low_s 401.high_s
add_group "$suite2026" fpspeed 500.low_s 501.high_s
add_group "$suite2017" fprate 600.low_r 601.high_r
add_group "$suite2017" intspeed 700.low_s 701.high_s
add_group "$suite2017" fpspeed 800.low_s 801.high_s
export SPEC_ORDER_OUTPUT="$output/results" SPEC_MATRIX_RUNNER="$root/tests/spec-ordered-runner.sh"
export SPEC_TIMEOUT=5 SPEC_REQUIRED_ABIS=system SPEC_ORDER_PORTABLE_FAIL=100.low_r
bash -n "$root/scripts/spec-ordered.sh"
bash "$root/scripts/spec-ordered.sh" "$suite2017" "$suite2026" "$output/toolchain" test > "$output/system-gate.log"
progress="$(sed -n 's/^Progress: //p' "$output/system-gate.log")"
test "$(wc -l < "$progress")" -eq 17
test "$(awk -F '\t' 'NR > 1 && $1 != previous { printf "%s ", $1; previous = $1 }' "$progress")" = '2017-intrate 2026-intrate 2026-fprate 2026-intspeed 2026-fpspeed 2017-fprate 2017-intspeed 2017-fpspeed '
awk -F '\t' '
    NR > 1 && NR % 2 == 0 && $2 !~ /low_/ { exit 1 }
    NR > 1 && NR % 2 == 1 && $2 !~ /high_/ { exit 1 }
    NR == 2 && ($3 != "PASS" || $4 != "FAIL") { exit 1 }
' "$progress"
run="$(dirname "$progress")"
cmp "$root/scripts/spec-ordered.sh" "$run/ordered.sh"
cmp "$root/scripts/spec-matrix.sh" "$run/matrix-source.sh"
cmp "$root/config/spec/llvmnet.cfg" "$run/llvmnet.cfg"
grep -qx 'result=COMPLETE' "$run/completion.txt"
grep -qx 'portable_failures=1' "$run/completion.txt"
exit_code=0
SPEC_REQUIRED_ABIS=both bash "$root/scripts/spec-ordered.sh" "$suite2017" "$suite2026" "$output/toolchain" test > "$output/both-gate.log" 2>&1 || exit_code=$?
test "$exit_code" -eq 1
progress="$(sed -n 's/^Progress: //p' "$output/both-gate.log")"
test "$(wc -l < "$progress")" -eq 2
grep -qx 'result=BLOCKED' "$(dirname "$progress")/completion.txt"
exit_code=0
SPEC_ORDER_PORTABLE_FAIL= SPEC_ORDER_FAIL=201.high_r bash "$root/scripts/spec-ordered.sh" "$suite2017" "$suite2026" "$output/toolchain" test > "$output/failed-system.log" 2>&1 || exit_code=$?
test "$exit_code" -eq 1
progress="$(sed -n 's/^Progress: //p' "$output/failed-system.log")"
test "$(wc -l < "$progress")" -eq 5
grep -qx 'benchmark=2026-intrate:201.high_r' "$(dirname "$progress")/completion.txt"
SPEC_ORDER_START=2026-fprate:300.low_r bash "$root/scripts/spec-ordered.sh" "$suite2017" "$suite2026" "$output/toolchain" test > "$output/resume.log"
progress="$(sed -n 's/^Progress: //p' "$output/resume.log")"
test "$(wc -l < "$progress")" -eq 13
awk -F '\t' 'NR == 2 && ($1 != "2026-fprate" || $2 != "300.low_r") { exit 1 }' "$progress"
exit_code=0
SPEC_ORDER_START=missing bash "$root/scripts/spec-ordered.sh" "$suite2017" "$suite2026" "$output/toolchain" test > "$output/missing.log" 2>&1 || exit_code=$?
test "$exit_code" -eq 2
printf 'PASS: exact SPEC group/numeric order, sequential ABI gates, four-thread speed settings, snapshots and explicit resume\n'