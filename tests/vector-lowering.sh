#!/usr/bin/env bash
# tags: core aot
# The typed vector lowering (ADR-16 Level 1, SV-02): tests/c/vector-extensions.c runs every vector shape and operation of
# Clang's vector extensions. Built at -O0 to -O3 in both modes it must print what native clang prints, under the JIT,
# with hardware intrinsics disabled (the software paths of Vector128) and as NativeAOT, and run as vector code: Vector128
# calls in the image. The hidden --vector-lowering=typed builds keep every function the lowering translates as vector
# code, which the default leaves to the scalarizer where lanes move more than they compute (most of this fixture's
# printing); there only the function whose operations have no lowering (__int128 lanes) is scalarized.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
source_file="$root/tests/c/vector-extensions.c"
vector_code() {
    local image="$1" calls
    calls="$(dotnet "$checks" vector-calls "$image" | sed -E 's/^vector-calls: ([0-9]+).*/\1/')"
    if (( calls < 500 )); then
        printf 'FAIL: %s makes %s Vector128 calls; the typed lowering was not used\n' "$image" "$calls" >&2
        exit 1
    fi
}
for level in -O0 -O1 -O2 -O3; do
    clang-22 "$level" -w "$source_file" -lm -o "$test_output/native$level"
    "$test_output/native$level" >"$test_output/native$level.txt"
    for runtime in portable system; do
        name="$runtime$level"
        "$llvmnet" --runtime="$runtime" "$level" -w "$source_file" -o "$test_output/$name.dll"
        dotnet "$test_output/$name.dll" >"$test_output/$name.txt"
        diff -u "$test_output/native$level.txt" "$test_output/$name.txt"
        vector_code "$test_output/$name.dll"
        name="$runtime$level-typed"
        "$llvmnet" --runtime="$runtime" "$level" --vector-lowering=typed -w -v "$source_file" -o "$test_output/$name.dll" 2>"$test_output/$name.log"
        dotnet "$test_output/$name.dll" >"$test_output/$name.txt"
        diff -u "$test_output/native$level.txt" "$test_output/$name.txt"
        vector_code "$test_output/$name.dll"
        scalarized="$(grep 'llvmnet: scalarized' "$test_output/$name.log" || true)"
        if [[ "$scalarized" != *"of fallback ("* || "$(wc -l <<<"$scalarized")" -ne 1 ]]; then
            printf 'FAIL: %s scalarized other functions than fallback:\n%s\n' "$name" "$scalarized" >&2
            exit 1
        fi
    done
done
for runtime in portable system; do
    DOTNET_EnableHWIntrinsic=0 dotnet "$test_output/$runtime-O2-typed.dll" >"$test_output/$runtime-software.txt"
    diff -u "$test_output/native-O2.txt" "$test_output/$runtime-software.txt"
done
"$llvmnet" --runtime=portable -O2 --vector-lowering=typed -w --nativeaot "$source_file" -o "$test_output/portable-aot" >"$test_output/portable-aot.log" 2>&1
"$test_output/portable-aot" | diff -u "$test_output/native-O2.txt" -
"$llvmnet" --runtime=system -O0 --vector-lowering=typed -w --nativeaot "$source_file" -o "$test_output/system-aot" >"$test_output/system-aot.log" 2>&1
"$test_output/system-aot" | diff -u "$test_output/native-O0.txt" -
# tests/ir/vector-lowering.ll (which ir.sh runs with the default lowering) with every translatable function typed.
for runtime in portable system; do
    tag_ir "$runtime" "$root/tests/ir/vector-lowering.ll" "$test_output/ir-$runtime.bc"
    "$llvmnet" --runtime="$runtime" --vector-lowering=typed "$test_output/ir-$runtime.bc" -o "$test_output/ir-$runtime.dll"
    dotnet "$test_output/ir-$runtime.dll"
done
"$llvmnet" --runtime=portable --vector-lowering=typed --nativeaot "$test_output/ir-portable.bc" -o "$test_output/ir-aot" >"$test_output/ir-aot.log" 2>&1
"$test_output/ir-aot"
printf 'PASS: vector extensions at -O0 to -O3 match native in both modes and both vector lowerings, under the JIT with and without hardware intrinsics and as NativeAOT, as Vector128 code, and the IR fixture typed\n'
