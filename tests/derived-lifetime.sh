#!/usr/bin/env bash
# tags: core aot fortran
# requires: flang
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/derived-lifetime"
mkdir -p "$output"
flang-22 -O1 -module-dir "$output" "$root/tests/fortran/derived-lifetime.f90" -o "$output/native"
"$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/derived-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 -module-dir "$output" "$root/tests/fortran/derived-lifetime.f90" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 -module-dir "$output" "$root/tests/fortran/derived-lifetime.f90" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s Fortran automatic allocatable-component cleanup in JIT/NativeAOT\n' "$runtime"
done