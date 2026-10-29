#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/ctype"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/ctype.c" -o "$output/native"
"$output/native"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$root/tests/c/ctype.c" -o "$output/ctype.dll"
dotnet "$output/ctype.dll"
printf 'PASS: C-locale ctype table ABI, EOF, case conversion and classifications\n'