#!/usr/bin/env bash
# tags: core aot cpp
# requires: clang++
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/cpp-filters"
mkdir -p "$output"
clang++-22 -std=c++14 -O1 "$root/tests/cpp/exception-filters.cpp" -o "$output/native"
"$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/filters-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -std=c++14 -O1 "$root/tests/cpp/exception-filters.cpp" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -std=c++14 -O1 "$root/tests/cpp/exception-filters.cpp" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s C++ exception specifications and cleanup in JIT/NativeAOT\n' "$runtime"
done
# Catch clauses: catch (...) after a typed handler, pointer values, qualification and base-class conversions.
clang++-22 -std=c++14 -O0 "$root/tests/cpp/catch-clauses.cpp" -o "$output/clauses-native"
"$output/clauses-native" > "$output/clauses-native.txt"
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -std=c++14 -O0 "$root/tests/cpp/catch-clauses.cpp" -o "$output/clauses-$runtime.dll"
    dotnet "$output/clauses-$runtime.dll" > "$output/clauses-$runtime.txt"
    diff -u "$output/clauses-native.txt" "$output/clauses-$runtime.txt"
done
printf 'PASS: catch (...) after a typed handler and pointer catches in both modes\n'
