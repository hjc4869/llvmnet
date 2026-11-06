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
# --vector-width=256: Vector256 fragments, with AVX and without (where .NET computes them as halves).
"$llvmnet" --runtime=portable -O2 --vector-width=256 --vector-lowering=typed -w "$source_file" -o "$test_output/portable-256.dll"
dotnet "$test_output/portable-256.dll" | diff -u "$test_output/native-O2.txt" -
DOTNET_EnableAVX=0 dotnet "$test_output/portable-256.dll" | diff -u "$test_output/native-O2.txt" -
vector_code "$test_output/portable-256.dll"
"$llvmnet" --runtime=portable -O2 --vector-lowering=typed -w --nativeaot "$source_file" -o "$test_output/portable-aot" >"$test_output/portable-aot.log" 2>&1
"$test_output/portable-aot" | diff -u "$test_output/native-O2.txt" -
"$llvmnet" --runtime=system -O0 --vector-lowering=typed -w --nativeaot "$source_file" -o "$test_output/system-aot" >"$test_output/system-aot.log" 2>&1
"$test_output/system-aot" | diff -u "$test_output/native-O0.txt" -
# Per-lane shifts (SV-24, tests/ir/vector-shifts.ll): the 12 shifts by variable counts call the lane helpers, which run
# as AVX2's or AdvSimd's shifts or lane by lane; the partial splats are shifts by one count and the constant counts per
# lane multiplications. With and without AVX2 and hardware intrinsics, at 256 bits and as NativeAOT at x86-64-v3.
tag_ir portable "$root/tests/ir/vector-shifts.ll" "$test_output/shifts.bc"
"$llvmnet" --runtime=portable "$test_output/shifts.bc" -o "$test_output/shifts.dll"
dotnet "$test_output/shifts.dll"
DOTNET_EnableAVX2=0 dotnet "$test_output/shifts.dll"
DOTNET_EnableHWIntrinsic=0 dotnet "$test_output/shifts.dll"
calls="$(dotnet "$checks" vector-calls "$test_output/shifts.dll")"
for expected in "Vectors.ShiftLeft 4," "Vectors.ShiftRightArithmetic 4," "Vectors.ShiftRightLogical 4," "op_Multiply 2,"; do
    if [[ "$calls" != *"$expected"* ]]; then
        printf 'FAIL: tests/ir/vector-shifts.ll: expected %s in %s\n' "$expected" "$calls" >&2
        exit 1
    fi
done
"$llvmnet" --runtime=portable --vector-width=256 "$test_output/shifts.bc" -o "$test_output/shifts-256.dll"
dotnet "$test_output/shifts-256.dll"
DOTNET_EnableAVX2=0 dotnet "$test_output/shifts-256.dll"
"$llvmnet" --runtime=portable --nativeaot --aot-instruction-set x86-64-v3 "$test_output/shifts.bc" -o "$test_output/shifts-aot" >"$test_output/shifts-aot.log" 2>&1
"$test_output/shifts-aot"
# The sign of each half of a lane spread over it, x264's abs2 (SV-39), is one arithmetic shift of the halves: only the
# fixture's three look-alikes multiply. With and without hardware intrinsics and at 256 bits.
tag_ir portable "$root/tests/ir/vector-sign-halves.ll" "$test_output/sign-halves.bc"
"$llvmnet" --runtime=portable "$test_output/sign-halves.bc" -o "$test_output/sign-halves.dll"
dotnet "$test_output/sign-halves.dll"
DOTNET_EnableHWIntrinsic=0 dotnet "$test_output/sign-halves.dll"
"$llvmnet" --runtime=portable --vector-width=256 "$test_output/sign-halves.bc" -o "$test_output/sign-halves-256.dll"
dotnet "$test_output/sign-halves-256.dll"
calls="$(dotnet "$checks" vector-calls "$test_output/sign-halves.dll")"
if [[ "$calls" != *"op_Multiply 3,"* ]]; then
    printf 'FAIL: tests/ir/vector-sign-halves.ll: expected op_Multiply 3 (the look-alikes) in %s\n' "$calls" >&2
    exit 1
fi
# The hidden --vector-lowering=scalar, the scalarizer's form of all vector code for comparisons: the shifts and the
# vectorizer's loops (masks built by insertelement chains, early exits) as native computes them.
"$llvmnet" --runtime=portable --vector-lowering=scalar "$test_output/shifts.bc" -o "$test_output/shifts-scalar.dll"
dotnet "$test_output/shifts-scalar.dll"
clang-22 -O3 "$root/tests/c/vector-loops.c" -o "$test_output/loops-native"
"$llvmnet" --runtime=portable -O3 --vector-lowering=scalar "$root/tests/c/vector-loops.c" -o "$test_output/loops-scalar.dll"
dotnet "$test_output/loops-scalar.dll" | diff -u <("$test_output/loops-native") -
# tests/ir/vector-lowering.ll (which ir.sh runs with the default lowering) with every translatable function typed.
for runtime in portable system; do
    tag_ir "$runtime" "$root/tests/ir/vector-lowering.ll" "$test_output/ir-$runtime.bc"
    "$llvmnet" --runtime="$runtime" --vector-lowering=typed -v "$test_output/ir-$runtime.bc" -o "$test_output/ir-$runtime.dll" 2>"$test_output/ir-$runtime.log"
    dotnet "$test_output/ir-$runtime.dll"
    # Vector getelementptrs are address arithmetic (SV-25): pointers stays vector code.
    if grep -q 'llvmnet: scalarized.* pointers\b' "$test_output/ir-$runtime.log"; then
        printf 'FAIL: tests/ir/vector-lowering.ll: pointers was scalarized\n' >&2
        exit 1
    fi
done
"$llvmnet" --runtime=portable --vector-lowering=typed --nativeaot "$test_output/ir-portable.bc" -o "$test_output/ir-aot" >"$test_output/ir-aot.log" 2>&1
"$test_output/ir-aot"
printf 'PASS: vector extensions at -O0 to -O3 match native in both modes and both vector lowerings, under the JIT with and without hardware intrinsics and as NativeAOT, as Vector128 code, and the IR fixture typed\n'
