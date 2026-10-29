#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/threads"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/threads.c" -pthread -o "$output/native"
"$output/native" >"$output/native.txt"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" -pthread "$root/tests/c/threads.c" -o "$output/threads.dll"
dotnet "$output/threads.dll" >"$output/cil.txt"
diff -u "$output/native.txt" "$output/cil.txt"
printf 'PASS: managed pthreads, contended atomics, condition wakeups, once, joins and TLS destructors\n'