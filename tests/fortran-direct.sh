#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/fortran-direct"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flang-22 -O1 "$root/tests/fortran/direct-io.f90" -o "$output/native"
for conversion in native big_endian; do
    timeout 30 "$output/native" "$output/native-$conversion.bin" "$conversion" > "$output/native-$conversion.txt"
    test "$(wc -c < "$output/native-$conversion.bin")" -eq 24
done
for runtime in system portable; do
    target="$output/direct-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/fortran/direct-io.f90" -o "$target.dll"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/fortran/direct-io.f90" -o "$target-aot"
    for conversion in native big_endian; do
        timeout 30 dotnet "$target.dll" "$target-$conversion.bin" "$conversion" > "$target-$conversion.txt"
        diff -u "$output/native-$conversion.txt" "$target-$conversion.txt"
        cmp "$output/native-$conversion.bin" "$target-$conversion.bin"
        timeout 30 "$target-aot" "$target-aot-$conversion.bin" "$conversion" > "$target-aot-$conversion.txt"
        diff -u "$output/native-$conversion.txt" "$target-aot-$conversion.txt"
        cmp "$output/native-$conversion.bin" "$target-aot-$conversion.bin"
    done
    printf 'PASS: %s Fortran direct-access unformatted records in JIT/NativeAOT\n' "$runtime"
done