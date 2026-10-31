#!/usr/bin/env bash
# tags: core
# requires: python3
# TC-17: docs/user/limitations.md is generated from the support matrix and the runtime's table of refused system
# calls. Fails and regenerates the page when it is out of date; review and commit the result.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
python3 "$root/scripts/limitations.py" "$llvmnet" "$root/docs/user/support-matrix.md" "$test_output/limitations.md"
if ! diff -u "$root/docs/user/limitations.md" "$test_output/limitations.md"; then
    cp "$test_output/limitations.md" "$root/docs/user/limitations.md"
    printf 'FAIL: docs/user/limitations.md was out of date; it has been regenerated, review and commit it\n' >&2
    exit 1
fi
# The refused calls return the errno the table states, as musl reports it.
"$llvmnet" --runtime=portable "$root/tests/c/unavailable.c" -o "$test_output/unavailable.dll"
dotnet "$test_output/unavailable.dll" >"$test_output/unavailable.txt"
grep -q '^fork ENOSYS$' "$test_output/unavailable.txt"
grep -q '^link EPERM$' "$test_output/unavailable.txt"
grep -q '^socket ENOSYS$' "$test_output/unavailable.txt"
grep -q '^epoll_create1 ENOSYS$' "$test_output/unavailable.txt"
grep -q '^shmget ENOSYS$' "$test_output/unavailable.txt"
grep -q '^setsid EPERM$' "$test_output/unavailable.txt"
printf 'PASS: the limitations page is current and the refused calls return their documented errno\n'
