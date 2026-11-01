#!/usr/bin/env bash
# tags: core aot
# Integer select, abs and min/max are computed without branches (RyuJIT turns branches into cmov only outside loops),
# except min/max against a constant bound, which keep a predictable branch:
# tests/c/select.c runs every comparison predicate and integer width on edge values and must print what the native build
# prints, under the JIT and NativeAOT, and its kernels (libpng's Paeth predictor among them) must have no branch in CIL.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
source_file="$root/tests/c/select.c"
clang-22 -O2 "$source_file" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
# The source still exercises what it is meant to: selects of each kind, and every integer min/max and abs intrinsic.
"$llvmnet" -O2 -S -emit-llvm -c "$source_file" -o "$test_output/select.ll"
for pattern in 'select i1 [^,]*, i1 ' 'select i1 [^,]*, i32 ' 'select i1 [^,]*, i64 ' 'select i1 [^,]*, ptr ' \
    'select i1 [^,]*, double ' 'llvm\.abs\.i32' 'llvm\.abs\.i64' 'llvm\.smin\.i8' 'llvm\.smax\.i17' 'llvm\.umin\.i32' \
    'llvm\.umax\.i33' 'llvm\.smin\.i64' 'llvm\.smax\.i32([^,]*, i32 0)' 'llvm\.umin\.i32([^,]*, i32 1023)'; do
    grep -q "$pattern" "$test_output/select.ll" || { printf 'FAIL: select.c no longer produces %s\n' "$pattern" >&2; exit 1; }
done
for runtime in system portable; do
    for level in -O0 -O2; do
        target="$test_output/$runtime$level"
        "$llvmnet" --runtime="$runtime" "$level" "$source_file" -o "$target.dll"
        dotnet "$target.dll" >"$target.txt"
        diff -u "$test_output/native.txt" "$target.txt"
    done
done
for function in paeth_predictor clip_pixel8 clamp_long select_flag select_wide; do
    opcodes="$(dotnet "$checks" il-opcodes "$test_output/portable-O2.dll" "$function")"
    if grep -Eq ' (br|brtrue|brfalse|beq|bne\.un|bge|bgt|ble|blt|bge\.un|bgt\.un|ble\.un|blt\.un|switch)(\.s)? ' <<<"$opcodes"; then
        printf 'FAIL: %s branches in CIL:\n%s\n' "$function" "$opcodes" >&2
        exit 1
    fi
done
# A minimum or maximum against a constant bound keeps its branch: a clamp is predictable (smax(x, 0), umin(x, 1023)).
dotnet "$checks" il-opcodes "$test_output/portable-O2.dll" clip_pixel10 | grep -Eq ' (brtrue|brfalse)(\.s)? '
"$llvmnet" --runtime=portable -O2 --nativeaot "$source_file" -o "$test_output/aot" >"$test_output/aot.log"
"$test_output/aot" >"$test_output/aot.txt"
diff -u "$test_output/native.txt" "$test_output/aot.txt"
printf 'PASS: branch-free select, abs and min/max match native in both modes under the JIT and NativeAOT\n'
