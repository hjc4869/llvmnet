#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/spec-matrix"
mkdir -p "$output"
suite="$(mktemp -d "$output/fake-suite.XXXXXXXX")"
mkdir -p "$suite/bin" "$suite/benchspec/CPU/100.pass_r/src" "$suite/benchspec/CPU/201.fail_s" "$output/toolchain"
touch "$suite/bin/runcpu"
chmod +x "$suite/bin/runcpu"
export SPEC_MATRIX_OUTPUT="$output/results"
export SPEC_MATRIX_RUNNER="$root/tests/spec-matrix-snapshot-runner.sh"
export SPEC_TIMEOUT=5 SPEC_OPT_LEVEL=3 SPEC_VECTORIZE=0
bash -n "$root/scripts/spec-matrix.sh" "$root/scripts/spec-harness.sh"
exit_code=0
bash "$root/scripts/spec-matrix.sh" "$suite" "$output/toolchain" test > "$output/output.txt" 2>&1 || exit_code=$?
test "$exit_code" -eq 1
results="$(sed -n 's/^Checking .* Results: //p' "$output/output.txt")"
test "$(wc -l < "$results")" -eq 5
run="$(dirname "$results")"
cmp "$root/scripts/spec-matrix.sh" "$run/matrix.sh"
cmp "$SPEC_MATRIX_RUNNER" "$run/runner.sh"
cmp "$root/config/spec/llvmnet.cfg" "$run/llvmnet.cfg"
grep -qx 'checks=4' "$run/completion.txt"
grep -qx 'failures=2' "$run/completion.txt"
awk -F '\t' '
    NR == 1 { next }
    $3 == "100.pass_r" && $7 != "PASS" { exit 1 }
    $3 == "201.fail_s" && $2 == "system" && ($7 != "FAIL" || $8 != 0) { exit 1 }
    $3 == "201.fail_s" && $2 == "portable" && ($7 != "TIMEOUT" || $8 != 124) { exit 1 }
    $5 != "-O3" || $6 != 0 { exit 1 }
    $10 != 1 || ($3 == "100.pass_r" && $11 != 1) || ($3 == "201.fail_s" && $11 != 4) { exit 1 }
    ($11 == 1 && $12 != "serial") || ($11 == 4 && $12 != "kit-openmp-v2") { exit 1 }
' "$results"
all_results="$results"
SPEC_REUSE_BUILD=1 bash "$root/scripts/spec-matrix.sh" "$suite" "$output/toolchain" test '100.*' > "$output/filtered.txt"
results="$(sed -n 's/^Checking .* Results: //p' "$output/filtered.txt")"
test "$(wc -l < "$results")" -eq 3
bash "$root/scripts/spec-summary.sh" "$all_results" "$results" > "$output/summary.tsv"
test "$(wc -l < "$output/summary.tsv")" -eq 3
awk -F '\t' 'BEGIN { OFS = FS } { NF = 9; print }' "$all_results" > "$output/legacy.tsv"
bash "$root/scripts/spec-summary.sh" "$output/legacy.tsv" "$all_results" > "$output/thread-profiles.tsv"
test "$(wc -l < "$output/thread-profiles.tsv")" -eq 4
awk -F '\t' '
    $2 == "201.fail_s" && $9 == 1 { historical++ }
    $2 == "201.fail_s" && $9 == 4 { threaded++ }
    END { if (historical != 1 || threaded != 1) exit 1 }
' "$output/thread-profiles.tsv"
awk -F '\t' 'BEGIN { OFS = FS } { NF = 11; print }' "$all_results" > "$output/configured-threads.tsv"
bash "$root/scripts/spec-summary.sh" "$output/configured-threads.tsv" "$all_results" > "$output/openmp-profiles.tsv"
test "$(wc -l < "$output/openmp-profiles.tsv")" -eq 4
awk -F '\t' '
    $2 == "201.fail_s" && $9 == 4 && $10 == "unspecified" { configured++ }
    $2 == "201.fail_s" && $9 == 4 && $10 == "kit-openmp-v2" { enabled++ }
    END { if (configured != 1 || enabled != 1) exit 1 }
' "$output/openmp-profiles.tsv"
awk -F '\t' 'BEGIN { OFS = FS } NR > 1 && $11 > 1 { $12 = "kit-openmp-v1" } { print }' "$all_results" > "$output/openmp-v1.tsv"
bash "$root/scripts/spec-summary.sh" "$output/openmp-v1.tsv" "$all_results" > "$output/concurrent-profiles.tsv"
test "$(wc -l < "$output/concurrent-profiles.tsv")" -eq 4
awk -F '\t' '
    $2 == "201.fail_s" && $10 == "kit-openmp-v1" { old++ }
    $2 == "201.fail_s" && $10 == "kit-openmp-v2" { current++ }
    END { if (old != 1 || current != 1) exit 1 }
' "$output/concurrent-profiles.tsv"
awk -F '\t' '
    NR == 2 && ($2 != "100.pass_r" || $6 != "PASS" || $7 != "PASS") { exit 1 }
    NR == 3 && ($2 != "201.fail_s" || $6 != "FAIL" || $7 != "TIMEOUT") { exit 1 }
' "$output/summary.tsv"
head -n 2 "$all_results" > "$output/partial.tsv"
bash "$root/scripts/spec-summary.sh" "$output/partial.tsv" > "$output/partial-summary.tsv"
awk -F '\t' 'NR == 2 && ($6 != "PASS" || $7 != "MISSING") { exit 1 }' "$output/partial-summary.tsv"
exit_code=0
bash "$root/scripts/spec-matrix.sh" "$suite" "$output/toolchain" test 'missing*' > "$output/missing.txt" 2>&1 || exit_code=$?
test "$exit_code" -eq 2
exit_code=0
SPEC_THREADS=0 bash "$root/scripts/spec-matrix.sh" "$suite" "$output/toolchain" test > "$output/invalid-threads.txt" 2>&1 || exit_code=$?
test "$exit_code" -eq 2
printf 'PASS: SPEC matrix snapshots, dual ABIs, one-copy/four-thread profiles, validation gates and thread-separated historical summaries\n'