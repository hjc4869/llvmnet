#!/usr/bin/env bash
# tags: core aot
# requires: python3
# timeout: 1800
# SV-04: the vector forms of the IR semantics probes (tests/semantics/vectors.py): every scalar probe family on
# 64- to 512-bit vectors of i8 to i64, float and double, masks, shuffles, lane access and reductions, with edge
# values, poison lanes masked and NaN canonicalized, compared bit for bit with the natively compiled module in both
# modes with every translatable function typed, and in portable mode with the default lowering, with hardware
# intrinsics disabled and as NativeAOT. VECTOR_SEMANTICS_SEED chooses the random lanes and shuffle masks.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
seed="${VECTOR_SEMANTICS_SEED:-20261103}"
python3 "$root/tests/semantics/vectors.py" "$test_output/native" "" "$seed" >/dev/null
modules=(vector-integer vector-float)
for module in "${modules[@]}"; do
    clang-22 -O0 "$test_output/native/$module.ll" -lm -o "$test_output/native/$module"
    "$test_output/native/$module" >"$test_output/native/$module.txt"
done
compare() {
    local label="$1" module="$2" actual="$3"
    if ! diff -q "$test_output/native/$module.txt" "$actual" >/dev/null; then
        printf 'FAIL: %s %s differs from native (seed %s); first mismatches (case, native, llvmnet):\n' "$label" "$module" "$seed" >&2
        diff "$test_output/native/$module.txt" "$actual" | grep '^[<>]' | head -20 >&2
        printf 'Case descriptions: %s/native/vector-cases.tsv\n' "$test_output" >&2
        exit 1
    fi
}
for runtime in system portable; do
    abi=dotnet64-v2
    [[ "$runtime" == system ]] && abi=system-linux-x64-v1
    directory="$test_output/$runtime"
    python3 "$root/tests/semantics/vectors.py" "$directory" "$abi" "$seed" >/dev/null
    for module in "${modules[@]}"; do
        "$llvmnet" --runtime="$runtime" --vector-lowering=typed "$directory/$module.ll" -o "$directory/$module.dll"
        dotnet "$directory/$module.dll" >"$directory/$module.txt"
        compare "$runtime JIT" "$module" "$directory/$module.txt"
        [[ "$runtime" == portable ]] || continue
        DOTNET_EnableHWIntrinsic=0 dotnet "$directory/$module.dll" >"$directory/$module-software.txt"
        compare "portable JIT without hardware intrinsics" "$module" "$directory/$module-software.txt"
        "$llvmnet" --runtime=portable "$directory/$module.ll" -o "$directory/$module-default.dll"
        dotnet "$directory/$module-default.dll" >"$directory/$module-default.txt"
        compare "portable JIT, default lowering" "$module" "$directory/$module-default.txt"
        "$llvmnet" --runtime=portable --vector-lowering=typed --nativeaot "$directory/$module.ll" -o "$directory/$module-aot" >"$directory/$module-aot.log"
        "$directory/$module-aot" >"$directory/$module-aot.txt"
        compare "portable NativeAOT" "$module" "$directory/$module-aot.txt"
    done
done
cases=$(wc -l <"$test_output/native/vector-cases.tsv")
printf 'PASS: %s vector IR probes match native in both modes, without hardware intrinsics, with the default lowering and as NativeAOT (seed %s)\n' "$cases" "$seed"
