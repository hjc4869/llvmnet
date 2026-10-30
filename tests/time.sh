#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/time"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/time.c" -o "$output/native"
"$output/native" >"$output/native.txt"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" "$root/tests/c/time.c" -o "$output/time-$runtime.dll"
    dotnet "$output/time-$runtime.dll" >"$output/cil-$runtime.txt"
    diff -u "$output/native.txt" "$output/cil-$runtime.txt"
done
printf 'PASS: tm layout, UTC conversion, leap day, strftime and monotonic time (system and portable)\n'
