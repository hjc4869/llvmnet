#!/usr/bin/env bash
# tags: core aot
# The widening reductions (SV-21): tests/c/vector-reductions.c sums absolute differences of bytes and products of 16-bit
# values in the shapes the loop and SLP vectorizers give them, which the backend runs as psadbw and pmaddwd through the
# x86 helpers, and has look-alikes it must leave alone. Every build must print what native clang prints: both modes at
# -O2 and -O3, without hardware intrinsics (the helpers' compositions), with 256-bit fragments with and without AVX,
# at --isa=x86-64-v3 (AVX2's forms), as NativeAOT, and with the recognition off (--vector-idioms=off). -v names the
# functions that got the instructions, which must be the expected ones.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
source_file="$root/tests/c/vector-reductions.c"
clang-22 -O2 "$source_file" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
clang-22 -O3 -march=x86-64-v3 "$source_file" -o "$test_output/native-v3"
"$test_output/native-v3" | diff -u "$test_output/native.txt" -
recognized="sad_u32 sad_u16 sad_select pix_abs16 pix_abs8 sad_vector_sum dot_i16 dot_u8_i16 dot_i8 dot_const sum_squares taps8 dot_vector_sum"
check_log() {
    local log="$1" function
    for function in $recognized; do
        if ! grep -q "^llvmnet: $function: " "$log"; then
            printf 'FAIL: %s: %s does not run as psadbw or pmaddwd\n' "$log" "$function" >&2
            exit 1
        fi
    done
    # The accumulators whose lanes the program stores, and products of unsigned 16-bit values, keep the literal code.
    for function in sad_vector dot_vector dot_u16; do
        if grep -q "^llvmnet: $function: " "$log"; then
            printf 'FAIL: %s: %s was rewritten\n' "$log" "$function" >&2
            exit 1
        fi
    done
}
for runtime in portable system; do
    for level in -O2 -O3; do
        name="$runtime$level"
        "$llvmnet" --runtime="$runtime" "$level" -v "$source_file" -o "$test_output/$name.dll" 2>"$test_output/$name.log"
        check_log "$test_output/$name.log"
        dotnet "$test_output/$name.dll" | diff -u "$test_output/native.txt" -
    done
    DOTNET_EnableHWIntrinsic=0 dotnet "$test_output/$runtime-O2.dll" | diff -u "$test_output/native.txt" -
done
"$llvmnet" -O2 --vector-width=256 -v "$source_file" -o "$test_output/width-256.dll" 2>"$test_output/width-256.log"
check_log "$test_output/width-256.log"
dotnet "$test_output/width-256.dll" | diff -u "$test_output/native.txt" -
DOTNET_EnableAVX=0 dotnet "$test_output/width-256.dll" | diff -u "$test_output/native.txt" -
"$llvmnet" -O3 --isa=x86-64-v3 -v "$source_file" -o "$test_output/v3.dll" 2>"$test_output/v3.log"
grep -q "^llvmnet: dot_vector_sum: " "$test_output/v3.log"
dotnet "$test_output/v3.dll" | diff -u "$test_output/native.txt" -
DOTNET_EnableAVX2=0 dotnet "$test_output/v3.dll" | diff -u "$test_output/native.txt" -
DOTNET_EnableHWIntrinsic=0 dotnet "$test_output/v3.dll" | diff -u "$test_output/native.txt" -
"$llvmnet" -O2 --vector-idioms=off -v "$source_file" -o "$test_output/literal.dll" 2>"$test_output/literal.log"
if grep -q "as psadbw\|as pmaddwd" "$test_output/literal.log"; then
    printf 'FAIL: --vector-idioms=off still recognized reductions\n' >&2
    exit 1
fi
dotnet "$test_output/literal.dll" | diff -u "$test_output/native.txt" -
for flags in "-O2" "-O3 --isa=x86-64-v3"; do
    name="aot${flags// /}"
    # shellcheck disable=SC2086
    "$llvmnet" $flags --nativeaot "$source_file" -o "$test_output/$name" >"$test_output/$name.log" 2>&1
    "$test_output/$name" | diff -u "$test_output/native.txt" -
done
printf 'PASS: sums of absolute differences and of 16-bit products run as psadbw and pmaddwd where only the reduction reads them, with native output in both modes, without hardware intrinsics, at 256 bits with and without AVX, at x86-64-v3 and as NativeAOT\n'
