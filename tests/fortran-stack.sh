#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/fortran-stack"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 -I/usr/lib/llvm-22/include/flang -c "$root/tests/c/descriptor-stack.c" -o "$output/native.o"
flang-22 "$output/native.o" -o "$output/native"
"$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/stack-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 -I/usr/lib/llvm-22/include/flang "$root/tests/c/descriptor-stack.c" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 -I/usr/lib/llvm-22/include/flang "$root/tests/c/descriptor-stack.c" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s Fortran descriptor stack in JIT/NativeAOT\n' "$runtime"
done