#!/usr/bin/env bash
# tags: core aot cpp
# requires: clang++
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/thread-local"
mkdir -p "$output"
clang++-22 -std=c++17 -pthread -O1 "$root/tests/cpp/thread-local.cpp" -o "$output/native"
timeout 30 "$output/native" > "$output/native.txt"
for runtime in system portable; do
    for optimization in 0 3; do
        target="$output/tls-$runtime-O$optimization"
        "$root/bin/llvmnet" --runtime="$runtime" -std=c++17 -O"$optimization" "$root/tests/cpp/thread-local.cpp" -o "$target.dll"
        timeout 30 dotnet "$target.dll" > "$target.txt"
        diff -u "$output/native.txt" "$target.txt"
        "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -std=c++17 -O"$optimization" "$root/tests/cpp/thread-local.cpp" -o "$target-aot"
        timeout 30 "$target-aot" > "$target-aot.txt"
        diff -u "$output/native.txt" "$target-aot.txt"
        printf 'PASS: %s O%s four-thread TLS initialization, isolation and destructors in JIT/NativeAOT\n' "$runtime" "$optimization"
    done
done