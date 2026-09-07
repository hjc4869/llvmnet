#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/atomics"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/atomics.c" -o "$output/native"
"$output/native"
for optimization in 0 1 2; do
    dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "-O$optimization" "$root/tests/c/atomics.c" -o "$output/atomics-O$optimization.dll"
    dotnet "$output/atomics-O$optimization.dll"
done
printf 'PASS: C11 atomic loads/stores, RMW, successful/failed CAS and wrapping\n'