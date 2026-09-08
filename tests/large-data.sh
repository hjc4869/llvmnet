#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/large-data"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/large-data.c" -o "$output/native"
"$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/data-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/c/large-data.c" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/c/large-data.c" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s globals larger than the CLR data-field limit in JIT/NativeAOT\n' "$runtime"
done