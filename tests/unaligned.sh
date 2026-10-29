#!/usr/bin/env bash
# tags: core aot
# Under-aligned, packed, overlapping and volatile memory access compared with native (BE-15).
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
fixture="$root/tests/c/unaligned.c"
for optimization in 0 2; do
    clang-22 -O"$optimization" "$fixture" -o "$test_output/native-O$optimization"
    "$test_output/native-O$optimization" >"$test_output/native-O$optimization.txt"
    for runtime in system portable; do
        "$llvmnet" --runtime="$runtime" -O"$optimization" "$fixture" -o "$test_output/unaligned-$runtime-O$optimization.dll"
        dotnet "$test_output/unaligned-$runtime-O$optimization.dll" >"$test_output/$runtime-O$optimization.txt"
        diff -u "$test_output/native-O$optimization.txt" "$test_output/$runtime-O$optimization.txt"
    done
done
"$llvmnet" --runtime=portable -O2 --nativeaot "$fixture" -o "$test_output/unaligned-aot" >"$test_output/aot.log"
"$test_output/unaligned-aot" >"$test_output/aot.txt"
diff -u "$test_output/native-O2.txt" "$test_output/aot.txt"
printf 'PASS: under-aligned scalars at every offset, packed records, overlapping moves and volatile access match native\n'
