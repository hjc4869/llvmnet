#!/usr/bin/env bash
# tags: core cpp
# requires: clang++
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/cpp"
mkdir -p "$output"
clang++-22 -std=c++17 -O1 "$root/tests/cpp/core.cpp" -o "$output/native"
"$output/native" >"$output/native.txt"
clang++-22 -std=c++17 -O1 "$root/tests/cpp/rtti.cpp" -o "$output/rtti-native"
"$output/rtti-native" >"$output/rtti-native.txt"
for runtime in system portable; do
    for optimization in 0 1 2; do
        "$llvmnet" --runtime="$runtime" -std=c++17 "-O$optimization" "$root/tests/cpp/core.cpp" -o "$output/cpp-$runtime-O$optimization.dll"
        dotnet "$output/cpp-$runtime-O$optimization.dll" >"$output/cil-$runtime.txt"
        diff -u "$output/native.txt" "$output/cil-$runtime.txt"
    done
    printf 'PASS: %s: C++ templates, vector, virtual dispatch, new/delete, guarded statics, typed catch, cleanup and rethrow\n' "$runtime"
    "$llvmnet" --runtime="$runtime" -std=c++17 "$root/tests/cpp/rtti.cpp" -o "$output/rtti-$runtime.dll"
    dotnet "$output/rtti-$runtime.dll" >"$output/rtti-$runtime.txt"
    diff -u "$output/rtti-native.txt" "$output/rtti-$runtime.txt"
    printf 'PASS: %s: C++ RTTI, cross/down casts, multiple/virtual inheritance and base-class catches\n' "$runtime"
done
