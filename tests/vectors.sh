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
clang-22 "$root/tests/ir/vector-invoke.ll" -lstdc++ -o "$output/invoke-native"
"$output/invoke-native"
cp "$root/tests/ir/vector-invoke.ll" "$output/invoke.input"
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
    "$root/bin/llvmnet" --runtime="$runtime" -x ir -O0 -c "$output/invoke.input" -o "$output/invoke-$runtime.bc"
    "$root/bin/llvmnet" --runtime="$runtime" "$output/invoke-$runtime.bc" -o "$output/invoke-$runtime.dll"
    dotnet "$output/invoke-$runtime.dll"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$output/invoke-$runtime.bc" -o "$output/invoke-$runtime-aot"
    "$output/invoke-$runtime-aot"
done
printf 'PASS: O3 vectorized arithmetic and vector invoke normal/unwind paths match native in both ABIs and JIT/NativeAOT\n'