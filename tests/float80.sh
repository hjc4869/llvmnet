#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/float80"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/float80.c" -lm -o "$output/native"
"$output/native"
for optimization in 0 1 2; do
    dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "-O$optimization" "$root/tests/c/float80.c" -o "$output/float80-O$optimization.dll"
    dotnet "$output/float80-O$optimization.dll"
done
printf 'PASS: extended precision, 80-bit constants, exponent range, subnormals and conversions\n'