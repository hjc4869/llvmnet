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
for notice in LICENSE.TXT THIRD-PARTY-NOTICES.md; do
    if [[ ! -s "$prefix/share/llvmnet/$notice" ]]; then
        printf 'FAIL: packaged toolchain is missing share/llvmnet/%s\n' "$notice" >&2
        exit 1
    fi
done
if [[ -d "$prefix/libexec/llvm" ]] && ! compgen -G "$prefix/share/llvmnet/licenses/*.copyright" >/dev/null; then
    printf 'FAIL: bundled LLVM has no copyright notices under share/llvmnet/licenses\n' >&2
    exit 1
fi
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