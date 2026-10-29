#!/usr/bin/env bash
# tags: core aot
# requires: python3
# timeout: 2400
# LLVM IR semantics audit (BE-11, BE-12): about 43,000 generated probes of integer, float, double and x86_fp80
# operations, casts, comparisons and intrinsics on edge values, widths 1 to 128 including odd widths,
# compared bit for bit with the natively compiled module in all modes, plus portable NativeAOT.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
python3 "$root/tests/semantics/generate.py" "$test_output/native" >/dev/null
for module in integer float extended; do
    clang-22 -O0 "$test_output/native/$module.ll" -lm -o "$test_output/native/$module"
    "$test_output/native/$module" >"$test_output/native/$module.txt"
done
compare() {
    local label="$1" module="$2" actual="$3"
    if ! diff -q "$test_output/native/$module.txt" "$actual" >/dev/null; then
        printf 'FAIL: %s %s differs from native; first mismatches (case, native, llvmnet):\n' "$label" "$module" >&2
        diff "$test_output/native/$module.txt" "$actual" | grep '^[<>]' | head -20 >&2
        printf 'Case descriptions: %s/native/cases.tsv\n' "$test_output" >&2
        exit 1
    fi
}
for runtime in managed-host system portable; do
    abi=""
    [[ "$runtime" == system ]] && abi=system-linux-x64-v1
    [[ "$runtime" == portable ]] && abi=dotnet64-v1
    directory="$test_output/$runtime"
    python3 "$root/tests/semantics/generate.py" "$directory" "$abi" >/dev/null
    for module in integer float extended; do
        "$llvmnet" --runtime="$runtime" "$directory/$module.ll" -o "$directory/semantics-$module.dll"
        dotnet "$directory/semantics-$module.dll" >"$directory/$module.txt"
        compare "$runtime JIT" "$module" "$directory/$module.txt"
        if [[ "$runtime" == portable ]]; then
            "$llvmnet" --runtime=portable --nativeaot "$directory/$module.ll" -o "$directory/semantics-$module-aot" >"$directory/aot-$module.log"
            "$directory/semantics-$module-aot" >"$directory/$module-aot.txt"
            compare "portable NativeAOT" "$module" "$directory/$module-aot.txt"
        fi
    done
done
cases=$(wc -l <"$test_output/native/cases.tsv")
printf 'PASS: %s integer and floating-point IR probes match native in all modes and portable NativeAOT\n' "$cases"
