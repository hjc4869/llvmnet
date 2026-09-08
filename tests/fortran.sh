#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/fortran"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 "$root/tests/fortran/core.f90" -o "$output/native"
"$output/native" >"$output/native.txt"
for optimization in 0 1 2 3; do
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
flang-22 -O3 "$root/tests/fortran/character-search.f90" -o "$output/search-native"
"$output/search-native" ababa
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -O3 -fvectorize -fslp-vectorize "$root/tests/fortran/character-search.f90" \
        --keep-ir "$output/search-$runtime.bc" -o "$output/search-$runtime.dll"
    dotnet "$output/search-$runtime.dll" ababa
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$output/search-$runtime.bc" -o "$output/search-$runtime-aot"
    "$output/search-$runtime-aot" ababa
done
printf 'PASS: O3 Fortran INDEX, REPEAT and ADJUSTL, empty strings and embedded NULs in both ABIs, JIT and NativeAOT\n'
flang-22 -O3 "$root/tests/fortran/backspace.f90" -o "$output/backspace-native"
(cd "$output" && ./backspace-native)
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -O3 "$root/tests/fortran/backspace.f90" \
        --keep-ir "$output/backspace-$runtime.bc" -o "$output/backspace-$runtime.dll"
    (cd "$output" && dotnet "backspace-$runtime.dll")
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$output/backspace-$runtime.bc" -o "$output/backspace-$runtime-aot"
    (cd "$output" && "./backspace-$runtime-aot")
done
printf 'PASS: O3 Fortran BACKSPACE, empty records, beginning/end-of-file and REWIND in both ABIs, JIT and NativeAOT\n'
flang-22 -O3 "$root/tests/fortran/pointers.f90" -o "$output/pointers-native"
"$output/pointers-native"
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -O3 "$root/tests/fortran/pointers.f90" \
        --keep-ir "$output/pointers-$runtime.bc" -o "$output/pointers-$runtime.dll"
    dotnet "$output/pointers-$runtime.dll"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$output/pointers-$runtime.bc" -o "$output/pointers-$runtime-aot"
    "$output/pointers-$runtime-aot"
done
printf 'PASS: O3 Fortran pointer bounds, allocation, aliases, reassociation, empty targets and deallocation in both ABIs, JIT and NativeAOT\n'
flang-22 -O3 "$root/tests/fortran/spread.f90" -o "$output/spread-native"
"$output/spread-native" 3
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -O3 "$root/tests/fortran/spread.f90" \
        --keep-ir "$output/spread-$runtime.bc" -o "$output/spread-$runtime.dll"
    dotnet "$output/spread-$runtime.dll" 3
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$output/spread-$runtime.bc" -o "$output/spread-$runtime-aot"
    "$output/spread-$runtime-aot" 3
done
printf 'PASS: O3 Fortran SPREAD scalar/strided arrays, dimension placement and empty results in both ABIs, JIT and NativeAOT\n'
flang-22 -O1 "$root/tests/fortran/remainders.f90" -o "$output/remainders-native"
"$output/remainders-native" > "$output/remainders-native.txt"
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/remainders.f90" -o "$output/remainders-$runtime.dll"
    dotnet "$output/remainders-$runtime.dll" > "$output/remainders-$runtime.txt"
    diff -u "$output/remainders-native.txt" "$output/remainders-$runtime.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/remainders.f90" -o "$output/remainders-$runtime-aot"
    "$output/remainders-$runtime-aot" > "$output/remainders-$runtime-aot.txt"
    diff -u "$output/remainders-native.txt" "$output/remainders-$runtime-aot.txt"
done
printf 'PASS: Fortran real MOD/MODULO signs and signed zero in both ABIs and JIT/NativeAOT\n'