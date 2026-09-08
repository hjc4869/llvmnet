#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/derived-assignment"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 -module-dir "$output" "$root/tests/fortran/derived-assignment.f90" -o "$output/native"
"$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/assignment-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 -module-dir "$output" "$root/tests/fortran/derived-assignment.f90" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 -module-dir "$output" "$root/tests/fortran/derived-assignment.f90" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s metadata-checked Fortran derived assignment in JIT/NativeAOT\n' "$runtime"
done