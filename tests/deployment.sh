#!/usr/bin/env bash
# tags: toolchain slow fortran cpp
# requires: flang
# serial: 95
# timeout: 2400
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
if [[ $# -gt 0 ]]; then
    prefix="$(realpath "$1")"
else
    # Without a package prefix argument, build a fresh self-contained package with bundled LLVM.
    prefix="$root/artifacts/tests/deployment/toolchain"
    rm -rf "$prefix"
    bash "$root/scripts/package-toolchain.sh" "$prefix" --self-contained --bundle-llvm
fi
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
# The CMake toolchain file works from the package: with no compiler given it finds the package's own drivers.
for file in cmake/llvmnet.cmake cmake/llvmnet-rules.cmake cmake/Platform/LlvmNet.cmake meson/llvmnet.ini; do
    if [[ ! -s "$prefix/share/llvmnet/$file" ]]; then
        printf 'FAIL: packaged toolchain is missing share/llvmnet/%s\n' "$file" >&2
        exit 1
    fi
done
mkdir -p "$output/cmake"
printf 'cmake_minimum_required(VERSION 3.20)\nproject(probe C)\nadd_executable(probe probe.c)\n' >"$output/cmake/CMakeLists.txt"
printf '#include <stdio.h>\nint main(void) { printf("%%d\\n", (int)sizeof(void *)); return 0; }\n' >"$output/cmake/probe.c"
rm -rf "$output/cmake-build"
cmake -G Ninja -S "$output/cmake" -B "$output/cmake-build" -DCMAKE_TOOLCHAIN_FILE="$prefix/share/llvmnet/cmake/llvmnet.cmake" \
    >"$output/cmake.log" 2>&1 && cmake --build "$output/cmake-build" >>"$output/cmake.log" 2>&1 ||
    { cat "$output/cmake.log" >&2; printf 'FAIL: the packaged CMake toolchain file did not build a C program\n' >&2; exit 1; }
grep -q "^set(CMAKE_C_COMPILER \"$prefix/bin/llvmnet-clang\")$" "$output"/cmake-build/CMakeFiles/*/CMakeCCompiler.cmake
test "$("$output/cmake-build/probe")" == 8
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