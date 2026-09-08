#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/nonadvancing"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 "$root/tests/fortran/nonadvancing.f90" -o "$output/native"
"$output/native" "$output/native.txt" > "$output/native.out"
for runtime in system portable; do
    target="$output/output-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/nonadvancing.f90" -o "$target.dll"
    dotnet "$target.dll" "$target.txt" > "$target.out"
    cmp "$output/native.txt" "$target.txt"
    diff -u "$output/native.out" "$target.out"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/nonadvancing.f90" -o "$target-aot"
    "$target-aot" "$target-aot.txt" > "$target-aot.out"
    cmp "$output/native.txt" "$target-aot.txt"
    diff -u "$output/native.out" "$target-aot.out"
    printf 'PASS: %s non-advancing formatted records in JIT/NativeAOT\n' "$runtime"
done
flang-22 -O1 "$root/tests/fortran/complex-io.f90" -o "$output/complex-native"
"$output/complex-native" "$output/complex-native.txt" > "$output/complex-native.out"
for runtime in system portable; do
    target="$output/complex-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/complex-io.f90" -o "$target.dll"
    dotnet "$target.dll" "$target.txt" > "$target.out"
    cmp "$output/complex-native.txt" "$target.txt"
    diff -u "$output/complex-native.out" "$target.out"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/complex-io.f90" -o "$target-aot"
    "$target-aot" "$target-aot.txt" > "$target-aot.out"
    cmp "$output/complex-native.txt" "$target-aot.txt"
    diff -u "$output/complex-native.out" "$target-aot.out"
done
flang-22 -O1 "$root/tests/fortran/logical-io.f90" -o "$output/logical-native"
"$output/logical-native" > "$output/logical-native.txt"
for runtime in system portable; do
    target="$output/logical-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/logical-io.f90" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/logical-native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/logical-io.f90" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/logical-native.txt" "$target-aot.txt"
    printf 'PASS: %s Fortran logical input in JIT/NativeAOT\n' "$runtime"
done