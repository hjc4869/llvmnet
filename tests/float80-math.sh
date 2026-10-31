#!/usr/bin/env bash
# tags: core aot
# The long double math intrinsics (BE-37): llvm.sqrt.f80, llvm.log.f80 and the others become calls to the C
# library's long double functions in the linker. tests/c/float80-math.c, built with -fno-math-errno so that Clang
# emits the intrinsics, must print what the native build prints in both modes under JIT and NativeAOT.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
source_file="$root/tests/c/float80-math.c"
clang-22 -O1 -fno-math-errno "$source_file" -lm -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" -O1 -fno-math-errno -c "$source_file" -o "$test_output/math-$runtime.bc"
    llvm-dis-22 "$test_output/math-$runtime.bc" -o - | grep -c '@llvm\.\(sqrt\|log\|exp\|pow\|sin\|rint\|frexp\|ldexp\|powi\)[a-z0-9]*\.f80' >/dev/null
    "$llvmnet" --runtime="$runtime" "$test_output/math-$runtime.bc" -o "$test_output/math-$runtime.dll"
    dotnet "$test_output/math-$runtime.dll" >"$test_output/math-$runtime.txt"
    diff -u "$test_output/native.txt" "$test_output/math-$runtime.txt"
    "$llvmnet" --runtime="$runtime" --nativeaot "$test_output/math-$runtime.bc" -o "$test_output/math-$runtime-aot" >"$test_output/math-$runtime-aot.log"
    "$test_output/math-$runtime-aot" >"$test_output/math-$runtime-aot.txt"
    diff -u "$test_output/native.txt" "$test_output/math-$runtime-aot.txt"
done
printf 'PASS: long double math intrinsics match native in both modes (JIT and NativeAOT)\n'
