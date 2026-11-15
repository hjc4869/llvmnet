#!/usr/bin/env bash
# tags: core
# A portable library that embeds the C library (-shared --embed-libc, XP-04), called from .NET without a program around
# it: its exports are the default-visibility functions of its own inputs, its static constructors run before the first
# call, and stdio, the heap, errno and thread_local work on the thread that called first and on other .NET threads.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
"$root/bin/llvmnet-clang++" -O2 -shared --embed-libc -fvisibility=hidden "$root/tests/cpp/embedded-libc.cpp" -o "$test_output/Embedded.dll"
dotnet "$checks" inspect "$test_output/Embedded.dll" >"$test_output/inspect.txt"
grep -qx 'exports: constructed format heap print thread_state' "$test_output/inspect.txt" ||
    { printf 'FAIL: unexpected exports\n' >&2; cat "$test_output/inspect.txt" >&2; exit 1; }
dotnet "$checks" call-exports "$test_output/Embedded.dll" constructed heap print format thread_state thread:thread_state thread_state thread:print \
    >"$test_output/calls.txt"
cat >"$test_output/expected.txt" <<'EXPECTED'
constructed=1042
heap=2893
hello from the embedded C library
print=34
format=1
thread_state=81
thread:thread_state=81
thread_state=91
hello from the embedded C library
thread:print=34
EXPECTED
diff -u "$test_output/expected.txt" "$test_output/calls.txt"
for invalid in "-c" "--runtime=system -shared" ""; do
    # shellcheck disable=SC2086
    if "$llvmnet" $invalid --embed-libc "$root/tests/c/smoke.c" -o "$test_output/invalid.dll" >"$test_output/invalid.log" 2>&1; then
        printf 'FAIL: --embed-libc was accepted with "%s"\n' "$invalid" >&2
        exit 1
    fi
    grep -q -- '--embed-libc requires -shared in portable mode' "$test_output/invalid.log"
done
printf 'PASS: a library embedding the C library runs its constructors and serves stdio, the heap, errno and thread_local to two .NET threads\n'
