#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/runtime"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
compiler=(dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll")
clang-22 "$root/tests/c/runtime.c" -o "$output/native"
"$output/native" payload >"$output/native.txt"
for optimization in 0 1 2; do
    "${compiler[@]}" "-O$optimization" "$root/tests/c/runtime.c" -o "$output/runtime-O$optimization.dll"
    dotnet "$output/runtime-O$optimization.dll" payload >"$output/managed-O$optimization.txt"
    diff -u "$output/native.txt" "$output/managed-O$optimization.txt"
done
printf 'PASS: argv, SysV variadics, va_copy, allocation, strings and printf at O0/O1/O2\n'