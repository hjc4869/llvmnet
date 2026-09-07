#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/vectors"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
flags=(-O3 -fvectorize -fslp-vectorize)
sources=("$root/tests/c/vector-main.c" "$root/tests/c/vector-kernel.c")
clang-22 "${flags[@]}" "${sources[@]}" -o "$output/native"
"$output/native"
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" "${flags[@]}" "${sources[@]}" --keep-ir "$output/$runtime.bc" -o "$output/vector-$runtime.dll"
    opt-22 -S "$output/$runtime.bc" -o "$output/$runtime.ll"
    grep -Eq '<[0-9]+ x (i[0-9]+|float|double)>' "$output/$runtime.ll"
    dotnet "$output/vector-$runtime.dll"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$output/$runtime.bc" -o "$output/$runtime-aot"
    "$output/$runtime-aot"
    "$root/bin/llvmnet" --runtime="$runtime" "${flags[@]}" -shared "$root/tests/c/vector-kernel.c" -o "$output/$runtime-kernel.dll"
    "$root/bin/llvmnet" --runtime="$runtime" "${flags[@]}" --cil-reference "$output/$runtime-kernel.dll" "$root/tests/c/vector-main.c" -o "$output/$runtime-client.dll"
    dotnet "$output/$runtime-client.dll"
done
printf 'PASS: O3 vectorized C arithmetic, reduction, floats, saturation and tails match native in both ABIs, JIT, NativeAOT and managed libraries\n'