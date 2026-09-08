#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/namelist"
mkdir -p "$output"
scratch="$(mktemp -d "$output/scratch.XXXXXXXX")"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 "$root/tests/fortran/namelist.f90" -o "$output/native"
TMPDIR="$scratch" "$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/namelist-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/namelist.f90" -o "$target.dll"
    TMPDIR="$scratch" dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/namelist.f90" -o "$target-aot"
    TMPDIR="$scratch" "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s Fortran namelist input and errors in JIT/NativeAOT\n' "$runtime"
done
rmdir "$scratch"
printf 'PASS: Fortran scratch files are unnamed and removed on close\n'
flang-22 -O1 "$root/tests/fortran/namelist-output.f90" -o "$output/output-native"
"$output/output-native" write "$output/output-native.nml" > "$output/output-native.txt"
for runtime in system portable; do
    target="$output/output-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/namelist-output.f90" -o "$target.dll"
    dotnet "$target.dll" read "$output/output-native.nml" > "$target-read.txt"
    diff -u "$output/output-native.txt" "$target-read.txt"
    dotnet "$target.dll" write "$target.nml" > "$target-write.txt"
    "$output/output-native" read "$target.nml" > "$target-native-read.txt"
    diff -u "$output/output-native.txt" "$target-native-read.txt"
    diff -u "$output/output-native.txt" "$target-write.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/namelist-output.f90" -o "$target-aot"
    "$target-aot" read "$output/output-native.nml" > "$target-aot-read.txt"
    diff -u "$output/output-native.txt" "$target-aot-read.txt"
    "$target-aot" write "$target-aot.nml" > "$target-aot-write.txt"
    "$output/output-native" read "$target-aot.nml" > "$target-aot-native-read.txt"
    diff -u "$output/output-native.txt" "$target-aot-native-read.txt"
    diff -u "$output/output-native.txt" "$target-aot-write.txt"
    printf 'PASS: %s namelist output and bidirectional native interchange in JIT/NativeAOT\n' "$runtime"
done