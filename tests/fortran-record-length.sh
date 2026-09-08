#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/fortran-record-length"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 "$root/tests/fortran/record-length.f90" -o "$output/native"
timeout 30 "$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/recl-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/record-length.f90" -o "$target.dll"
    timeout 30 dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/record-length.f90" -o "$target-aot"
    timeout 30 "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s Fortran record-length limits in JIT/NativeAOT\n' "$runtime"
done