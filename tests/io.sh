#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/io"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/io.c" -o "$output/native"
"$output/native" "$output/native.bin" >"$output/native.txt"
for runtime in system portable; do
    for optimization in 0 1 2; do
        "$llvmnet" --runtime="$runtime" "-O$optimization" "$root/tests/c/io.c" -o "$output/io-O$optimization-$runtime.dll"
        dotnet "$output/io-O$optimization-$runtime.dll" "$output/cil-$runtime.bin" >"$output/cil-$runtime.txt"
        diff -u "$output/native.txt" "$output/cil-$runtime.txt"
    done
done
printf 'PASS: stdio, file descriptors, stat layout, errno, parsing, scanf and managed qsort callbacks (system and portable)\n'
