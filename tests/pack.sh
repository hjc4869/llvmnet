#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/pack"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
for fixture in pack reshape array-constructor deferred-character transfer; do
    flang-22 -O3 "$root/tests/fortran/$fixture.f90" -o "$output/$fixture-native"
    "$output/$fixture-native" > "$output/$fixture-native.txt"
    for runtime in system portable; do
        target="$output/$fixture-$runtime"
        "$root/bin/llvmnet" --runtime="$runtime" -O3 "$root/tests/fortran/$fixture.f90" -o "$target.dll"
        dotnet "$target.dll" > "$target.txt"
        diff -u "$output/$fixture-native.txt" "$target.txt"
        "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O3 "$root/tests/fortran/$fixture.f90" -o "$target-aot"
        "$target-aot" > "$target-aot.txt"
        diff -u "$output/$fixture-native.txt" "$target-aot.txt"
        printf 'PASS: %s Fortran %s array transforms in JIT/NativeAOT\n' "$runtime" "$fixture"
    done
done