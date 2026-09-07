#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/fortran"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 "$root/tests/fortran/core.f90" -o "$output/native"
"$output/native" >"$output/native.txt"
for optimization in 0 1 2; do
    "$root/bin/llvmnet" "-O$optimization" "$root/tests/fortran/core.f90" -o "$output/fortran-O$optimization.dll"
    dotnet "$output/fortran-O$optimization.dll" >"$output/cil.txt"
    diff -u "$output/native.txt" "$output/cil.txt"
done
printf 'PASS: Flang to CIL, Fortran main, arrays, real/integer arithmetic, recursion and bind(C)\n'
flang-22 -O1 "$root/tests/fortran/io.f90" -o "$output/io-native"
"$root/bin/llvmnet" "$root/tests/fortran/io.f90" -o "$output/io.dll"
(
    cd "$output"
    ./io-native payload >io-native.txt
    dotnet io.dll payload >io-cil.txt
    diff -u io-native.txt io-cil.txt
)
printf 'PASS: Fortran allocatables, internal records, formatted/list I/O, NEWUNIT and sequential files\n'
"$root/bin/llvmnet" -c "$root/tests/fortran/arithmetic.f90" -o "$output/arithmetic.o"
test -f "$output/arithmetic.mod"
"$root/bin/llvmnet" -I"$output" "$root/tests/fortran/module-main.f90" "$output/arithmetic.o" -o "$output/module-main.dll"
dotnet "$output/module-main.dll"
"$root/bin/llvmnet" -E "$root/tests/fortran/arithmetic.f90" -o "$output/arithmetic-preprocessed.f90"
grep -qi 'module arithmetic' "$output/arithmetic-preprocessed.f90"
printf 'PASS: separate Fortran modules, persistent module outputs and Flang preprocessing\n'