#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$1"
mkdir -p "$output"
if [[ ! -f "$output/libllvmnet-llvm.so" || "$root/native/constants.cpp" -nt "$output/libllvmnet-llvm.so" || "$root/native/linker.cpp" -nt "$output/libllvmnet-llvm.so" || "$root/native/abi.cpp" -nt "$output/libllvmnet-llvm.so" ]]; then
    read -r -a include_flags <<<"$(llvm-config-22 --cxxflags)"
    read -r -a link_flags <<<"$(llvm-config-22 --ldflags)"
    read -r -a libraries <<<"$(llvm-config-22 --libs core bitreader bitwriter irreader linker object)"
    clang++-22 -shared -fPIC -O2 "${include_flags[@]}" "$root/native/constants.cpp" "$root/native/linker.cpp" "$root/native/abi.cpp" "${link_flags[@]}" "${libraries[@]}" -o "$output/libllvmnet-llvm.so"
fi