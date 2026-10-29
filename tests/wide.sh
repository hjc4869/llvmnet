#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/wide"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/int128.c" -o "$output/native"
"$output/native" >"$output/native.txt"
for optimization in 0 1 2; do
    "$root/bin/llvmnet" "-O$optimization" "$root/tests/c/int128.c" -o "$output/wide-O$optimization.dll"
    dotnet "$output/wide-O$optimization.dll" >"$output/cil.txt"
    diff -u "$output/native.txt" "$output/cil.txt"
done
printf 'PASS: signed/unsigned 128-bit arithmetic, shifts, division, constants and floating conversions\n'