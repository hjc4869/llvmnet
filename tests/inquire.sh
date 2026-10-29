#!/usr/bin/env bash
# tags: core aot fortran
# requires: flang
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$(mktemp -d "$root/artifacts/tests/inquire.XXXXXXXX")"
flang-22 -O1 "$root/tests/fortran/inquire.f90" -o "$output/native"
"$output/native" "$output/native.bin" > "$output/native.txt"
for runtime in system portable; do
    target="$output/inquire-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/inquire.f90" -o "$target.dll"
    dotnet "$target.dll" "$target.bin" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/inquire.f90" -o "$target-aot"
    "$target-aot" "$target-aot.bin" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s Fortran file/unit inquiry and stream positions in JIT/NativeAOT\n' "$runtime"
done