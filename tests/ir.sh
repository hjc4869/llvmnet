#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/ir"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
for source in "$root"/tests/ir/*.ll; do
    name="$(basename "$source" .ll)"
    clang-22 "$source" -lm -o "$output/$name-native"
    "$output/$name-native"
    dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$source" -o "$output/$name.dll"
    dotnet "$output/$name.dll"
    printf 'PASS: native and CIL LLVM IR %s\n' "$name"
done