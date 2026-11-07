#!/usr/bin/env bash
# tags: core aot
# Static allocas and the variadic call area in frame slots (PF-24): tests/c/frame-slots.c must print what the native
# build prints, under the JIT at -O0 and -O2 and as NativeAOT.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
source_file="$root/tests/c/frame-slots.c"
clang-22 -O1 "$source_file" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
for optimization in 0 2; do
    "$llvmnet" --runtime=portable -O"$optimization" "$source_file" -o "$test_output/O$optimization.dll"
    dotnet "$test_output/O$optimization.dll" >"$test_output/O$optimization.txt"
    diff -u "$test_output/native.txt" "$test_output/O$optimization.txt"
done
"$llvmnet" --runtime=portable --nativeaot -O2 "$source_file" -o "$test_output/aot" >"$test_output/aot.log"
"$test_output/aot" >"$test_output/aot.txt"
diff -u "$test_output/native.txt" "$test_output/aot.txt"
printf 'PASS: frame slots match native under the JIT and NativeAOT\n'
