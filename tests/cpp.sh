#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/cpp"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang++-22 -std=c++17 -O1 "$root/tests/cpp/core.cpp" -o "$output/native"
"$output/native" >"$output/native.txt"
for optimization in 0 1 2; do
    "$root/bin/llvmnet" -std=c++17 "-O$optimization" "$root/tests/cpp/core.cpp" -o "$output/cpp-O$optimization.dll"
    dotnet "$output/cpp-O$optimization.dll" >"$output/cil.txt"
    diff -u "$output/native.txt" "$output/cil.txt"
done
printf 'PASS: C++ templates, vector, virtual dispatch, new/delete, guarded statics, typed catch, cleanup and rethrow\n'
clang++-22 -std=c++17 -O1 "$root/tests/cpp/rtti.cpp" -o "$output/rtti-native"
"$output/rtti-native" >"$output/rtti-native.txt"
"$root/bin/llvmnet" -std=c++17 "$root/tests/cpp/rtti.cpp" -o "$output/rtti.dll"
dotnet "$output/rtti.dll" >"$output/rtti-cil.txt"
diff -u "$output/rtti-native.txt" "$output/rtti-cil.txt"
printf 'PASS: C++ RTTI, cross/down casts, multiple/virtual inheritance and base-class catches\n'