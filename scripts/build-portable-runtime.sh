#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bash "$root/scripts/install-libcxx-headers.sh"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
mkdir -p "$root/artifacts/portable-runtime" "$root/sysroot/dotnet64-v1/lib"
"$root/bin/llvmnet" --runtime=portable -std=c++17 -O1 -c \
    "$root/sysroot/dotnet64-v1/src/cxx-runtime.cpp" -o "$root/artifacts/portable-runtime/cxx-runtime.o"
"$root/bin/llvmnet" --runtime=portable -std=c++17 -O1 -c \
    "$root/sysroot/dotnet64-v1/src/cxx-new.cpp" -o "$root/artifacts/portable-runtime/cxx-new.o"
llvm-ar-22 rcs "$root/sysroot/dotnet64-v1/lib/libcxx-managed.a" "$root/artifacts/portable-runtime/cxx-runtime.o" "$root/artifacts/portable-runtime/cxx-new.o"
printf 'Built portable libc++ support archive as ABI-tagged LLVM bitcode.\n'