#!/usr/bin/env bash
# tags: core aot
# Aggregates through variadic calls (BE-19): structs passed in registers, in memory and after the registers are
# used up, and a byval fixed parameter; tests/c/varargs-aggregates.c must print what the native build prints in
# both modes under JIT and NativeAOT.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
source_file="$root/tests/c/varargs-aggregates.c"
clang-22 -O1 "$source_file" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
for runtime in system portable; do
    for optimization in 0 2; do
        "$llvmnet" --runtime="$runtime" -O"$optimization" "$source_file" -o "$test_output/$runtime-O$optimization.dll"
        dotnet "$test_output/$runtime-O$optimization.dll" >"$test_output/$runtime-O$optimization.txt"
        diff -u "$test_output/native.txt" "$test_output/$runtime-O$optimization.txt"
    done
    "$llvmnet" --runtime="$runtime" --nativeaot -O1 "$source_file" -o "$test_output/$runtime-aot" >"$test_output/$runtime-aot.log"
    "$test_output/$runtime-aot" >"$test_output/$runtime-aot.txt"
    diff -u "$test_output/native.txt" "$test_output/$runtime-aot.txt"
done
printf 'PASS: aggregates through variadic calls match native in both modes (JIT and NativeAOT)\n'
