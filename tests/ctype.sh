#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/ctype"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/ctype.c" -o "$output/native"
"$output/native"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$root/tests/c/ctype.c" -o "$output/ctype.dll"
dotnet "$output/ctype.dll"
printf 'PASS: C-locale ctype table ABI, EOF, case conversion and classifications\n'