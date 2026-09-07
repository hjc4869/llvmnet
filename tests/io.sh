#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/io"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/io.c" -o "$output/native"
"$output/native" "$output/native.bin" >"$output/native.txt"
for optimization in 0 1 2; do
    dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "-O$optimization" "$root/tests/c/io.c" -o "$output/io-O$optimization.dll"
    dotnet "$output/io-O$optimization.dll" "$output/cil.bin" >"$output/cil.txt"
    diff -u "$output/native.txt" "$output/cil.txt"
done
printf 'PASS: stdio, file descriptors, stat layout, errno, parsing, scanf and managed qsort callbacks\n'