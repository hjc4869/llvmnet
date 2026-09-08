#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/fortran-endfile"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 "$root/tests/fortran/endfile.f90" -o "$output/native"
"$output/native" "$output/native-records" > "$output/native.txt"
for runtime in system portable; do
    target="$output/endfile-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/endfile.f90" -o "$target.dll"
    dotnet "$target.dll" "$target-records" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    cmp "$output/native-records.txt" "$target-records.txt"
    cmp "$output/native-records.bin" "$target-records.bin"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/endfile.f90" -o "$target-aot"
    "$target-aot" "$target-aot-records" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    cmp "$output/native-records.txt" "$target-aot-records.txt"
    cmp "$output/native-records.bin" "$target-aot-records.bin"
    printf 'PASS: %s Fortran ENDFILE positions and truncation in JIT/NativeAOT\n' "$runtime"
done