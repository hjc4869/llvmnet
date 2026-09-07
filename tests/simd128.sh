#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/simd128"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
for runtime in system portable; do
    abi=system-linux-x64-v1
    if [[ "$runtime" == portable ]]; then abi=dotnet64-v1; fi
    sed "s/LLVMNET_TEST_ABI/$abi/" "$root/tests/wasm/simd128-helpers.ll" >"$output/$runtime.ll"
    "$root/bin/llvmnet" --runtime="$runtime" --simd128 "$output/$runtime.ll" -o "$output/simd-$runtime.dll"
    dotnet "$output/simd-$runtime.dll"
    DOTNET_EnableHWIntrinsic=0 dotnet "$output/simd-$runtime.dll"
    "$root/bin/llvmnet" --runtime="$runtime" --simd128 --nativeaot "$output/$runtime.ll" -o "$output/simd-$runtime-aot"
    "$output/simd-$runtime-aot"
done
oracle="$root/artifacts/tests/wasm-simd128"
bash "$root/tests/wasm-simd128.sh"
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" --simd128 -O2 "$root/tests/wasm/simd128.c" "$root/tests/wasm/simd128-main.c" \
        --keep-ir "$output/oracle-$runtime.bc" -o "$output/oracle-$runtime.dll"
    dotnet "$output/oracle-$runtime.dll" "$oracle/inputs.bin" >"$output/$runtime.txt"
    diff -u "$oracle/wasm.txt" "$output/$runtime.txt"
    DOTNET_EnableHWIntrinsic=0 dotnet "$output/oracle-$runtime.dll" "$oracle/inputs.bin" >"$output/$runtime-software.txt"
    diff -u "$oracle/wasm.txt" "$output/$runtime-software.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --simd128 --nativeaot "$output/oracle-$runtime.bc" -o "$output/oracle-$runtime-aot"
    "$output/oracle-$runtime-aot" "$oracle/inputs.bin" >"$output/$runtime-aot.txt"
    diff -u "$oracle/wasm.txt" "$output/$runtime-aot.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --simd128 --nativeaot --aot-instruction-set native "$output/oracle-$runtime.bc" -o "$output/oracle-$runtime-aot-host"
    "$output/oracle-$runtime-aot-host" "$oracle/inputs.bin" >"$output/$runtime-aot-host.txt"
    diff -u "$oracle/wasm.txt" "$output/$runtime-aot-host.txt"
done
printf 'PASS: SIMD128 helper calls preserve vectors in both ABIs, JIT, software fallback and NativeAOT\n'