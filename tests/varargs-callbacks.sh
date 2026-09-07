#!/usr/bin/env bash
set -euo pipefail
ulimit -c 0
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/varargs-callbacks"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/varargs-callbacks.c" -o "$output/native"
"$output/native" >"$output/native.txt"
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/c/varargs-callbacks.c" -o "$output/callbacks-$runtime.dll"
    dotnet "$output/callbacks-$runtime.dll" >"$output/$runtime.txt"
    diff -u "$output/native.txt" "$output/$runtime.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/c/varargs-callbacks.c" -o "$output/callbacks-$runtime-aot"
    "$output/callbacks-$runtime-aot" >"$output/$runtime-aot.txt"
    diff -u "$output/native.txt" "$output/$runtime-aot.txt"
done
clang-22 -shared -fPIC "$root/tests/c/varargs-native.c" -o "$output/libvariadic.so"
for mode in jit aot; do
    options=()
    if [[ "$mode" == aot ]]; then options=(--nativeaot); fi
    "$root/bin/llvmnet" --runtime=system "${options[@]}" -DTEST_NATIVE_CALLBACK \
        --system-library "$output/libvariadic.so" "$root/tests/c/varargs-callbacks.c" -o "$output/unsupported-$mode"
    if timeout 30s "$output/unsupported-$mode" >"$output/unsupported-$mode.log" 2>&1; then
        printf 'FAIL: native variadic callback did not fail explicitly\n' >&2
        exit 1
    fi
    grep -q 'Native invocation of variadic callback sum is not supported' "$output/unsupported-$mode.log"
done
printf 'PASS: internal variadic callbacks, identity, mixed register/stack arguments, NativeAOT and explicit native-boundary failure\n'