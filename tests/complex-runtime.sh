#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/complex-runtime"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/complex-runtime.c" -lm -o "$output/native"
"$output/native" > "$output/native.txt"
"$root/bin/llvmnet" --runtime=system -O1 "$root/tests/c/complex-runtime.c" -o "$output/complex.dll"
dotnet "$output/complex.dll" > "$output/jit.txt"
diff -u "$output/native.txt" "$output/jit.txt"
"$root/bin/llvmnet" --runtime=system --nativeaot -O1 "$root/tests/c/complex-runtime.c" -o "$output/complex-aot"
"$output/complex-aot" > "$output/aot.txt"
diff -u "$output/native.txt" "$output/aot.txt"
printf 'PASS: system complex runtime multiplication/division and exceptional values in JIT/NativeAOT\n'