#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/toolchain"
mkdir -p "$output"
bash "$root/scripts/package-toolchain.sh" "$output/install"
relocated="$output/relocated toolchain"
mkdir -p "$relocated"
cp -a "$output/install/." "$relocated/"
compiler="$relocated/bin/llvmnet-clang"
"$compiler" "$root/tests/c/core.c" -o "$output/normal executable"
"$output/normal executable"
"$compiler" -c "$root/tests/c/link-library.c" -o "$output/library.o"
"$relocated/bin/llvmnet-ar" rcs "$output/libexample.a" "$output/library.o"
"$compiler" "$root/tests/c/link-main.c" -L"$output" -lexample -o "$output/archive-executable"
"$output/archive-executable"
"$compiler" --nativeaot "$root/tests/c/core.c" -o "$output/aot-executable"
"$output/aot-executable"
printf 'PASS: relocated toolchain, executable outputs, archive utilities and self-contained NativeAOT\n'