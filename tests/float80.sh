#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/float80"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/float80.c" -lm -o "$output/native"
"$output/native"
for optimization in 0 1 2; do
    dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "-O$optimization" "$root/tests/c/float80.c" -o "$output/float80-O$optimization.dll"
    dotnet "$output/float80-O$optimization.dll"
done
printf 'PASS: extended precision, 80-bit constants, exponent range, subnormals and conversions\n'
clang-22 -O1 "$root/tests/c/float80-floor.c" -lm -o "$output/floor-native"
"$output/floor-native" > "$output/floor-native.txt"
for runtime in system portable; do
    target="$output/floor-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 -c "$root/tests/c/float80-floor.c" -o "$target.bc"
    llvm-dis-22 "$target.bc" -o - | grep -q '@llvm.floor.f80'
    "$root/bin/llvmnet" --runtime="$runtime" "$target.bc" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/floor-native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$target.bc" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/floor-native.txt" "$target-aot.txt"
    printf 'PASS: %s Float80 floor precision, signed zero and exceptional values in JIT/NativeAOT\n' "$runtime"
done