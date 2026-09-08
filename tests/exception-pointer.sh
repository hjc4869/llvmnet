#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/exception-pointer"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang++-22 -std=c++17 -pthread -O1 "$root/tests/cpp/exception-pointer.cpp" -o "$output/native"
timeout 30 "$output/native" > "$output/native.txt"
for optimization in 0 3; do
    target="$output/exception-O$optimization"
    "$root/bin/llvmnet" --runtime=system -std=c++17 -O"$optimization" "$root/tests/cpp/exception-pointer.cpp" -o "$target.dll"
    timeout 30 dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime=system --nativeaot -std=c++17 -O"$optimization" "$root/tests/cpp/exception-pointer.cpp" -o "$target-aot"
    timeout 30 "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: system O%s exception_ptr lifetime and concurrent rethrow in JIT/NativeAOT\n' "$optimization"
done