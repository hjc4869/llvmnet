#!/usr/bin/env bash
# tags: core aot
# Signaling NaN constants keep their bits at -O0, where LLVM does not fold them away: float, double, x86_fp80 and a
# float copied through memory, compared bit for bit with the native build in both modes and portable NativeAOT.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
clang-22 -O0 "$root/tests/c/signaling-nan.c" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" -O0 "$root/tests/c/signaling-nan.c" -o "$test_output/snan-$runtime.dll"
    dotnet "$test_output/snan-$runtime.dll" >"$test_output/$runtime.txt"
    diff -u "$test_output/native.txt" "$test_output/$runtime.txt"
done
"$llvmnet" --runtime=portable --nativeaot -O0 "$root/tests/c/signaling-nan.c" -o "$test_output/snan-aot" >"$test_output/aot.log"
"$test_output/snan-aot" >"$test_output/aot.txt"
diff -u "$test_output/native.txt" "$test_output/aot.txt"
printf 'PASS: signaling NaN constants keep their bits in both modes under JIT and portable NativeAOT\n'
