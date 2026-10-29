#!/usr/bin/env bash
# tags: core aot fortran
# requires: flang
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/fortran-model"
mkdir -p "$output"
flang-22 -O1 "$root/tests/fortran/real-model.f90" -o "$output/native"
"$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/model-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/real-model.f90" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/real-model.f90" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s Fortran real-model inquiries in JIT/NativeAOT\n' "$runtime"
done