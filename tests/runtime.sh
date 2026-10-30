#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/runtime"
mkdir -p "$output"
clang-22 "$root/tests/c/runtime.c" -o "$output/native"
"$output/native" payload >"$output/native.txt"
for runtime in system portable; do
    for optimization in 0 1 2; do
        "$llvmnet" --runtime="$runtime" "-O$optimization" "$root/tests/c/runtime.c" -o "$output/runtime-O$optimization-$runtime.dll"
        dotnet "$output/runtime-O$optimization-$runtime.dll" payload >"$output/managed-$runtime-O$optimization.txt"
        diff -u "$output/native.txt" "$output/managed-$runtime-O$optimization.txt"
    done
done
printf 'PASS: argv, SysV variadics, va_copy, allocation, strings and printf at O0/O1/O2 (system and portable)\n'
