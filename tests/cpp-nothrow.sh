#!/usr/bin/env bash
# tags: core aot cpp
# requires: clang++
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/cpp-nothrow"
mkdir -p "$output"
clang++-22 -std=c++14 -O1 "$root/tests/cpp/nothrow.cpp" -o "$output/native"
"$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/nothrow-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -std=c++14 -O1 "$root/tests/cpp/nothrow.cpp" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -std=c++14 -O1 "$root/tests/cpp/nothrow.cpp" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s nothrow allocation, failure and constructor cleanup in JIT/NativeAOT\n' "$runtime"
done