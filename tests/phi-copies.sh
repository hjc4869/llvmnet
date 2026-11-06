#!/usr/bin/env bash
# tags: core aot
# requires: python3
# Phi copies in functions that share locals (PF-22): tests/c/phi-copies.py generates dispatch loops of more than 512
# LLVM values whose cases rotate variables in cycles and swaps, forward them through branches, inner loops and shared
# switch targets, and keep copies live past the loop. Each program must print what its native build prints in both
# modes under the JIT and NativeAOT, and its phis must share the locals of the values they copy.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
for seed in 1 2 3; do
    program="$test_output/seed$seed"
    python3 "$root/tests/c/phi-copies.py" "$seed" >"$program.c"
    clang-22 -O2 "$program.c" -o "$program-native"
    "$program-native" 20000 >"$program-native.txt"
    for runtime in system portable; do
        "$llvmnet" --runtime="$runtime" -O2 --diagnose-limits "$program.c" -o "$program-$runtime.dll" 2>"$program-$runtime.log"
        dotnet "$program-$runtime.dll" 20000 >"$program-$runtime.txt"
        diff -u "$program-native.txt" "$program-$runtime.txt"
    done
    # The 40 variables' phis and the values they copy take about 75 locals; without coalescing, run had 175 to 180.
    locals="$(awk '$1 == "llvmnet:" && $2 == "limits:" && $NF == "run" { print $6 }' "$program-portable.log")"
    if [[ -z "$locals" || "$locals" -ge 110 ]]; then
        printf 'FAIL: run of seed %s has %s locals, expected fewer than 110\n' "$seed" "${locals:-no}" >&2
        exit 1
    fi
done
for seed in 1 2; do
    program="$test_output/seed$seed"
    "$llvmnet" --runtime=portable -O2 --nativeaot "$program.c" -o "$program-aot" >"$program-aot.log"
    "$program-aot" 20000 >"$program-aot.txt"
    diff -u "$program-native.txt" "$program-aot.txt"
done
printf 'PASS: dispatch loops with coalesced phi copies match native in both modes under the JIT and NativeAOT\n'
