#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/fortran-convert"
mkdir -p "$output"
unset FORT_CONVERT
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 -fconvert=big-endian "$root/tests/fortran/conversion-default.f90" -o "$output/native"
"$output/native" "$output/native-big.bin" > "$output/native.txt"
FORT_CONVERT=LITTLE_ENDIAN "$output/native" "$output/native-little.bin" > "$output/native-little.txt"
test "$(od -An -tx1 -N4 "$output/native-big.bin" | tr -d ' \n')" = 00000008
test "$(od -An -tx1 -N4 "$output/native-little.bin" | tr -d ' \n')" = 08000000
for runtime in system portable; do
    target="$output/convert-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 -fconvert=big-endian "$root/tests/fortran/conversion-default.f90" -o "$target.dll"
    dotnet "$target.dll" "$target-big.bin" > "$target.txt"
    cmp "$output/native-big.bin" "$target-big.bin"
    diff -u "$output/native.txt" "$target.txt"
    FORT_CONVERT=LITTLE_ENDIAN dotnet "$target.dll" "$target-little.bin" > "$target-little.txt"
    cmp "$output/native-little.bin" "$target-little.bin"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 -fconvert=big-endian "$root/tests/fortran/conversion-default.f90" -o "$target-aot"
    "$target-aot" "$target-aot-big.bin" > "$target-aot.txt"
    cmp "$output/native-big.bin" "$target-aot-big.bin"
    diff -u "$output/native.txt" "$target-aot.txt"
    FORT_CONVERT=LITTLE_ENDIAN "$target-aot" "$target-aot-little.bin" > "$target-aot-little.txt"
    cmp "$output/native-little.bin" "$target-aot-little.bin"
    printf 'PASS: %s compiler conversion defaults and environment overrides in JIT/NativeAOT\n' "$runtime"
done