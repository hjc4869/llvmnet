#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/time"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/time.c" -o "$output/native"
"$output/native" >"$output/native.txt"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$root/tests/c/time.c" -o "$output/time.dll"
dotnet "$output/time.dll" >"$output/cil.txt"
diff -u "$output/native.txt" "$output/cil.txt"
printf 'PASS: tm layout, UTC conversion, leap day, strftime and monotonic time\n'