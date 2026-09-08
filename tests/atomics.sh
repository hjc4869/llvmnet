#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/atomics"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/atomics.c" -o "$output/native"
"$output/native"
for optimization in 0 1 2; do
    dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "-O$optimization" "$root/tests/c/atomics.c" -o "$output/atomics-O$optimization.dll"
    dotnet "$output/atomics-O$optimization.dll"
done
printf 'PASS: C11 atomic loads/stores, RMW, successful/failed CAS and wrapping\n'
clang-22 -O1 -pthread "$root/tests/c/atomic-floating.c" -o "$output/floating-native"
"$output/floating-native" > "$output/floating-native.txt"
for runtime in system portable; do
    for optimization in 0 3; do
        target="$output/floating-$runtime-O$optimization"
        "$root/bin/llvmnet" --runtime="$runtime" -O"$optimization" -c "$root/tests/c/atomic-floating.c" -o "$target.bc"
        llvm-dis-22 "$target.bc" -o - | grep 'atomicrmw fadd' > /dev/null
        "$root/bin/llvmnet" --runtime="$runtime" "$target.bc" -o "$target.dll"
        timeout 30 dotnet "$target.dll" > "$target.txt"
        diff -u "$output/floating-native.txt" "$target.txt"
        "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$target.bc" -o "$target-aot"
        timeout 30 "$target-aot" > "$target-aot.txt"
        diff -u "$output/floating-native.txt" "$target-aot.txt"
        printf 'PASS: %s floating atomic add/sub O%s and four-thread contention in JIT/NativeAOT\n' "$runtime" "$optimization"
    done
done