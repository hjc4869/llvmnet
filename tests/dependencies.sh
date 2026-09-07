#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
prefix="${LLVMNET_DEPS_PREFIX:-$root/artifacts/deps}"
output="$root/artifacts/tests/dependencies"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
for name in "${@:-zlib libxml2}"; do
    for library in $name; do
        case "$library" in
            zlib) archive="$prefix/lib/libz.a" ;;
            libxml2) archive="$prefix/lib/libxml2.a" ;;
            dav1d) archive="$prefix/lib/libdav1d.a" ;;
            *) printf 'Unknown dependency test: %s\n' "$library" >&2; exit 2 ;;
        esac
        "$root/bin/llvmnet" -I"$prefix/include" -I"$prefix/include/libxml2" \
            "$root/tests/dependencies/$library.c" "$archive" -lm --keep-ir "$output/$library.bc" -o "$output/$library.dll"
        clang-22 -O1 "$output/$library.bc" -lm -o "$output/$library-native"
        "$output/$library-native" >"$output/$library-native.txt"
        dotnet "$output/$library.dll" >"$output/$library-cil.txt"
        diff -u "$output/$library-native.txt" "$output/$library-cil.txt"
        printf 'PASS: native and CIL %s integration\n' "$library"
    done
done