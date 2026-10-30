#!/usr/bin/env bash
# Build libllvmnet-llvm.so, the compiler's small C++ bridge to LLVM, into the directory given as $1.
# Invoked by the BuildLlvmBridge MSBuild target. Rebuilds only when a hash of the sources, this script, the C++
# compiler path and the LLVM configuration changes.
#   LLVM_CONFIG   llvm-config of LLVM 22 (default: llvm-config-22)
#   LLVMNET_CXX   C++ compiler (default: clang++-22)
#   LLVMNET_NATIVE_BUILD=cmake   build through native/CMakeLists.txt instead of a direct compile
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:?Usage: build-native.sh output-directory}"
llvm_config="${LLVM_CONFIG:-llvm-config-22}"
cxx="${LLVMNET_CXX:-clang++-22}"
required_major=22
fail() {
    printf 'error: %s\n' "$1" >&2
    printf 'llvmnet needs the LLVM %s development files to build its native bridge. On Debian/Ubuntu install llvm-%s-dev and clang-%s, or set LLVM_CONFIG and LLVMNET_CXX.\n' "$required_major" "$required_major" "$required_major" >&2
    exit 1
}
command -v "$llvm_config" >/dev/null 2>&1 || fail "$llvm_config was not found."
command -v "$cxx" >/dev/null 2>&1 || fail "C++ compiler $cxx was not found."
version="$("$llvm_config" --version)"
[[ "${version%%.*}" == "$required_major" ]] || fail "$llvm_config reports LLVM $version; llvmnet $(<"$root/Directory.Build.props" grep -o '<VersionPrefix>[^<]*' | cut -d'>' -f2) requires LLVM $required_major."
include_dir="$("$llvm_config" --includedir)"
[[ -f "$include_dir/llvm-c/Core.h" && -f "$include_dir/llvm/Linker/Linker.h" ]] || fail "LLVM headers are missing from $include_dir."
sources=("$root/native/constants.cpp" "$root/native/linker.cpp" "$root/native/abi.cpp" "$root/native/fp.cpp")
read -r -a include_flags <<<"$("$llvm_config" --cxxflags)"
read -r -a link_flags <<<"$("$llvm_config" --ldflags)"
read -r -a libraries <<<"$("$llvm_config" --libs core bitreader bitwriter irreader linker object)"
mkdir -p "$output"
library="$output/libllvmnet-llvm.so"
stamp="$output/libllvmnet-llvm.stamp"
hash="$( { cat "${sources[@]}" "${BASH_SOURCE[0]}" "$root/native/CMakeLists.txt"; printf '%s\n' "$version" "$(command -v "$cxx")" "${include_flags[*]}" "${link_flags[*]}" "${libraries[*]}" "${LLVMNET_NATIVE_BUILD:-direct}"; } | sha256sum | cut -d' ' -f1)"
if [[ -f "$library" && -f "$stamp" && "$(<"$stamp")" == "$hash" ]]; then exit 0; fi
if [[ "${LLVMNET_NATIVE_BUILD:-direct}" == cmake ]]; then
    build="$output/native-build"
    cmake -S "$root/native" -B "$build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_COMPILER="$cxx" \
        -DLLVM_DIR="$("$llvm_config" --cmakedir)" >/dev/null
    cmake --build "$build" >/dev/null
    cp "$build/libllvmnet-llvm.so" "$library.new"
else
    "$cxx" -shared -fPIC -O2 "${include_flags[@]}" "${sources[@]}" "${link_flags[@]}" "${libraries[@]}" -o "$library.new"
fi
mv "$library.new" "$library"
printf '%s\n' "$hash" >"$stamp"
