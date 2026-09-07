#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/system-abi"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
for fixture in runtime io threads posix; do
    "$root/bin/llvmnet" --runtime=system -O1 "$root/tests/c/$fixture.c" -o "$output/$fixture"
    case "$fixture" in
        runtime) "$output/$fixture" payload ;;
        io) "$output/$fixture" "$output/file.bin" ;;
        posix) "$output/$fixture" "$output/directory" ;;
        *) "$output/$fixture" ;;
    esac
done
clang-22 -shared -fPIC "$root/tests/c/system-library.c" -o "$output/libexternal.so"
"$root/bin/llvmnet" --runtime=system --system-library "$output/libexternal.so" \
    "$root/tests/c/system-library-client.c" -o "$output/external-client"
"$output/external-client"
"$root/bin/llvmnet" --runtime=system --nativeaot --system-library "$output/libexternal.so" \
    "$root/tests/c/system-library-client.c" -o "$output/external-client-aot"
"$output/external-client-aot"
"$root/bin/llvmnet" --runtime=system -c "$root/tests/c/link-main.c" -o "$output/system.o"
"$root/bin/llvmnet" -c "$root/tests/c/link-library.c" -o "$output/compat.o"
if "$root/bin/llvmnet" --runtime=system "$output/system.o" "$output/compat.o" -o "$output/mixed" 2>"$output/mismatch.log"; then
    printf 'FAIL: incompatible ABI modes were linked\n' >&2
    exit 1
fi
grep -q 'ABI mode mismatch' "$output/mismatch.log"
printf 'PASS: system libc/stdio, native varargs, qsort and pthread callbacks, OS mappings, and ABI-mix rejection\n'