#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/unformatted"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 "$root/tests/fortran/unformatted.f90" -o "$output/native"
"$output/native" "$output/native.bin" > "$output/native.txt"
for runtime in system portable; do
    target="$output/unformatted-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/unformatted.f90" -o "$target.dll"
    dotnet "$target.dll" "$target.bin" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    cmp "$output/native.bin" "$target.bin"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/unformatted.f90" -o "$target-aot"
    "$target-aot" "$target-aot.bin" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    cmp "$output/native.bin" "$target-aot.bin"
    printf 'PASS: %s unformatted record bytes, I/O, backspace and error handling in JIT/NativeAOT\n' "$runtime"
done
flang-22 -O1 "$root/tests/fortran/derived-io.f90" -o "$output/derived-native"
"$output/derived-native" "$output/derived-native.bin" > "$output/derived-native.txt"
for runtime in system portable; do
    target="$output/derived-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/derived-io.f90" -o "$target.dll"
    dotnet "$target.dll" "$target.bin" > "$target.txt"
    diff -u "$output/derived-native.txt" "$target.txt"
    cmp "$output/derived-native.bin" "$target.bin"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/derived-io.f90" -o "$target-aot"
    "$target-aot" "$target-aot.bin" > "$target-aot.txt"
    diff -u "$output/derived-native.txt" "$target-aot.txt"
    cmp "$output/derived-native.bin" "$target-aot.bin"
    printf 'PASS: %s nested derived-type component I/O without padding in JIT/NativeAOT\n' "$runtime"
done