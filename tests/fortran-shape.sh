#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/fortran-shape"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O0 "$root/tests/fortran/shape.f90" -o "$output/native"
"$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/shape-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O0 -c "$root/tests/fortran/shape.f90" -o "$target.bc"
    llvm-dis-22 "$target.bc" -o - | grep '@_FortranASizeDim' > /dev/null
    "$root/bin/llvmnet" --runtime="$runtime" "$target.bc" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$target.bc" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s Fortran SIZE dimension inquiries in JIT/NativeAOT\n' "$runtime"
done
flang-22 -O1 "$root/tests/fortran/pointer-association.f90" -o "$output/association-native"
"$output/association-native" > "$output/association-native.txt"
for runtime in system portable; do
    target="$output/association-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/pointer-association.f90" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/association-native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/pointer-association.f90" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/association-native.txt" "$target-aot.txt"
    printf 'PASS: %s Fortran pointer target association in JIT/NativeAOT\n' "$runtime"
done