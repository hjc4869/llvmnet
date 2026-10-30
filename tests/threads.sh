#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/threads"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/threads.c" -pthread -o "$output/native"
"$output/native" >"$output/native.txt"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" -pthread "$root/tests/c/threads.c" -o "$output/threads-$runtime.dll"
    dotnet "$output/threads-$runtime.dll" >"$output/cil-$runtime.txt"
    diff -u "$output/native.txt" "$output/cil-$runtime.txt"
done
printf 'PASS: managed pthreads, contended atomics, condition wakeups, once, joins and TLS destructors (system and portable)\n'
