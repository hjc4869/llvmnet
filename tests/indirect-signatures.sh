#!/usr/bin/env bash
# tags: core aot
# Indirect calls of many signatures (PF-27): tests/c/indirect-signatures.c must print what the native build prints,
# in both modes under the JIT and as NativeAOT, and as NativeAOT with --check-indirect-calls.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
source_file="$root/tests/c/indirect-signatures.c"
clang-22 -O2 "$source_file" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" -O2 "$source_file" -o "$test_output/calls-$runtime.dll"
    dotnet "$test_output/calls-$runtime.dll" >"$test_output/calls-$runtime.txt"
    diff -u "$test_output/native.txt" "$test_output/calls-$runtime.txt"
    "$llvmnet" --runtime="$runtime" --nativeaot -O2 "$source_file" -o "$test_output/$runtime-aot" >"$test_output/$runtime-aot.log"
    "$test_output/$runtime-aot" >"$test_output/$runtime-aot.txt"
    diff -u "$test_output/native.txt" "$test_output/$runtime-aot.txt"
done
"$llvmnet" --runtime=portable --nativeaot -O2 --check-indirect-calls "$source_file" -o "$test_output/checked-aot" >"$test_output/checked-aot.log"
"$test_output/checked-aot" >"$test_output/checked-aot.txt"
diff -u "$test_output/native.txt" "$test_output/checked-aot.txt"
printf 'PASS: indirect calls of every signature match native under the JIT and NativeAOT\n'
