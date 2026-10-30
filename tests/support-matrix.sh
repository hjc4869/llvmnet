#!/usr/bin/env bash
# tags: core
# requires: python3
# Regenerate the LLVM IR support matrix and fail when docs/user/support-matrix.md is stale or
# any construct causes an internal compiler error. Run "tests/run.sh support-matrix" and commit the result.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
python3 "$root/scripts/support-matrix.py" "$llvmnet" "$test_output/probes" "$test_output/support-matrix.md"
if ! diff -u "$root/docs/user/support-matrix.md" "$test_output/support-matrix.md"; then
    cp "$test_output/support-matrix.md" "$root/docs/user/support-matrix.md"
    printf 'FAIL: docs/user/support-matrix.md was out of date; it has been regenerated, review and commit it\n' >&2
    exit 1
fi
printf 'PASS: support matrix is current\n'
