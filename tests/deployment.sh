#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
prefix="${1:-$root/artifacts/deploy-toolchain}"
prefix="$(realpath "$prefix")"
output="$root/artifacts/tests/deployment"
mkdir -p "$output/essentials"
for utility in bash dirname basename; do
    ln -sfn "$(command -v "$utility")" "$output/essentials/$utility"
done
for language in c cpp fortran; do
    case "$language" in
        c) compiler=llvmnet-clang; input="$root/tests/c/portable.c" ;;
        cpp) compiler=llvmnet-clang++; input="$root/tests/cpp/portable.cpp" ;;
        fortran) compiler=llvmnet-flang; input="$root/tests/fortran/core.f90" ;;
    esac
    PATH="$output/essentials" "$prefix/bin/$compiler" --runtime=portable "$input" -o "$output/$language.dll"
    dotnet "$output/$language.dll"
done
printf 'PASS: bundled C/C++/Fortran compilation without dotnet, clang or flang on PATH\n'