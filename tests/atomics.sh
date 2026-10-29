#!/usr/bin/env bash
# tags: core aot
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/atomics"
mkdir -p "$output"
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
clang-22 -O2 -pthread "$root/tests/c/atomics-contention.c" -latomic -o "$output/contention-native"
"$output/contention-native" >"$output/contention-native.txt"
clang-22 -O1 "$root/tests/c/atomics-wide.c" -latomic -o "$output/wide-native"
"$output/wide-native" >"$output/wide-native.txt"
for runtime in system portable; do
    for fixture in contention wide; do
        source_file="$root/tests/c/atomics-$fixture.c"
        "$root/bin/llvmnet" --runtime="$runtime" -O2 -pthread "$source_file" -o "$output/$fixture-$runtime.dll" 2>"$output/$fixture-$runtime.log"
        timeout 60 dotnet "$output/$fixture-$runtime.dll" >"$output/$fixture-$runtime.txt"
        diff -u "$output/$fixture-native.txt" "$output/$fixture-$runtime.txt"
        "$root/bin/llvmnet" --runtime="$runtime" -O2 -pthread --nativeaot "$source_file" -o "$output/$fixture-$runtime-aot" >"$output/$fixture-$runtime-aot.log" 2>&1
        timeout 60 "$output/$fixture-$runtime-aot" >"$output/$fixture-$runtime-aot.txt"
        diff -u "$output/$fixture-native.txt" "$output/$fixture-$runtime-aot.txt"
    done
done
printf 'PASS: eight-thread atomics on every width and operation, under-aligned and 128-bit atomics, both ABIs, JIT and NativeAOT\n'
