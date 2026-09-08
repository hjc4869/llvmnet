#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/stack"
mkdir -p "$output"
ulimit -S -s 8192
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 "$root/tests/ir/stack-save.ll" -o "$output/native"
"$output/native"
cp "$root/tests/ir/stack-save.ll" "$output/stack.input"
for runtime in system portable; do
    target="$output/stack-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -x ir -O0 -c "$output/stack.input" -o "$target.bc"
    "$root/bin/llvmnet" --runtime="$runtime" "$target.bc" -o "$target.dll"
    dotnet "$target.dll"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$target.bc" -o "$target-aot"
    "$target-aot"
    printf 'PASS: %s nested stack save/restore, alignment and bounded allocation in JIT/NativeAOT\n' "$runtime"
done
clang-22 -O3 "$root/tests/c/stack-jumps.c" -o "$output/jumps-native"
"$output/jumps-native" > "$output/jumps-native.txt"
for runtime in system portable; do
    target="$output/jumps-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O3 "$root/tests/c/stack-jumps.c" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/jumps-native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O3 "$root/tests/c/stack-jumps.c" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/jumps-native.txt" "$target-aot.txt"
    printf 'PASS: %s longjmp restores dynamic allocations in JIT/NativeAOT\n' "$runtime"
done