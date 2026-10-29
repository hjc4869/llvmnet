#!/usr/bin/env bash
# tags: core
# --check-indirect-calls (BE-18): compatible pointer casts run, an incompatible one fails with both
# signatures and the target's name instead of undefined behavior.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" -O0 --check-indirect-calls "$root/tests/c/indirect-calls.c" -o "$test_output/checked-$runtime.dll"
    dotnet "$test_output/checked-$runtime.dll"
    if dotnet "$test_output/checked-$runtime.dll" mismatch >"$test_output/$runtime.out" 2>"$test_output/$runtime.err"; then
        printf 'FAIL: %s mismatched indirect call was not detected\n' "$runtime" >&2
        exit 1
    fi
    grep -q 'Indirect call with CIL signature Int32(Int32) reached widen, whose signature is Int64(Int64)' "$test_output/$runtime.err"
done
printf 'PASS: --check-indirect-calls accepts compatible pointer casts and reports incompatible targets in both ABIs\n'
