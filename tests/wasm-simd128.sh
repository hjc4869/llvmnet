#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/wasm-simd128"
mkdir -p "$output"
linker="${WASM_LD:-$(command -v wasm-ld-22 || command -v wasm-ld || command -v wasm-ld-21 || true)}"
if [[ -z "$linker" ]]; then
    printf 'Set WASM_LD to a WebAssembly linker executable.\n' >&2
    exit 1
fi
clang-22 --target=wasm32 -msimd128 -O2 -c "$root/tests/wasm/simd128.c" -o "$output/oracle.o"
"$linker" --no-entry --export=run --export=input_address --export=output_address \
    --export-memory "$output/oracle.o" -o "$output/oracle.wasm"
node "$root/tests/wasm/simd128.mjs" "$output/oracle.wasm" "$output/inputs.bin" >"$output/wasm.txt"
dotnet build "$root/tests/LlvmNet.Checks" -c Release --nologo
checks="$root/tests/LlvmNet.Checks/bin/Release/net10.0/LlvmNet.Checks.dll"
dotnet "$checks" simd128-probe "$output/inputs.bin" >"$output/dotnet.txt" 2>"$output/hardware.log"
diff -u "$output/wasm.txt" "$output/dotnet.txt"
DOTNET_EnableHWIntrinsic=0 dotnet "$checks" simd128-probe "$output/inputs.bin" \
    >"$output/software.txt" 2>"$output/software.log"
grep -q '^Vector128.IsHardwareAccelerated=False$' "$output/software.log"
diff -u "$output/wasm.txt" "$output/software.txt"
dotnet publish "$root/tests/LlvmNet.Checks" -c Release -p:PublishAot=true -o "$output/aot" --nologo \
    >"$output/aot.log" 2>&1
"$output/aot/LlvmNet.Checks" simd128-probe "$output/inputs.bin" >"$output/aot.txt" 2>"$output/aot-hardware.log"
diff -u "$output/wasm.txt" "$output/aot.txt"
printf 'PASS: 21 SIMD128 operations over 67 input records match WebAssembly in .NET JIT, software fallback and NativeAOT\n'