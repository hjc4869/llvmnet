#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
packages="${LIGHTPLAYER_PACKAGES:-/mnt/storage/david/Development/LightPlayer-Packages}"
prefix="${LLVMNET_DEPS_PREFIX:-$root/artifacts/deps}"
build="$root/artifacts/deps-build"
export PATH="$root/bin:$PATH"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
mkdir -p "$build" "$prefix"
dependency="${1:-all}"
if [[ "$dependency" == all || "$dependency" == zlib ]]; then
    zlib_source="${ZLIB_SOURCE:-$(em-config CACHE)/ports/zlib/zlib-1.3.2}"
    if [[ ! -f "$zlib_source/CMakeLists.txt" ]]; then
        printf 'Set ZLIB_SOURCE to the zlib 1.3.2 source directory.\n' >&2
        exit 1
    fi
    cmake -G Ninja -S "$zlib_source" -B "$build/zlib" \
        -DCMAKE_TOOLCHAIN_FILE="$root/cmake/llvmnet.cmake" \
        -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_FLAGS_RELEASE='-O1 -DNDEBUG' \
        -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib \
        -DZLIB_BUILD_SHARED=OFF -DZLIB_BUILD_STATIC=ON -DZLIB_BUILD_TESTING=OFF
    cmake --build "$build/zlib" --parallel "${JOBS:-8}"
    cmake --install "$build/zlib"
fi
if [[ "$dependency" == all || "$dependency" == libxml2 ]]; then
    cmake -G Ninja -S "$packages/libxml2" -B "$build/libxml2" \
        -DCMAKE_TOOLCHAIN_FILE="$root/cmake/llvmnet.cmake" \
        -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_FLAGS_RELEASE='-O1 -DNDEBUG' \
        -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib \
        -DBUILD_SHARED_LIBS=OFF -DLIBXML2_WITH_DOCS=OFF \
        -DLIBXML2_WITH_ICONV=OFF -DLIBXML2_WITH_ICU=OFF -DLIBXML2_WITH_MODULES=OFF \
        -DLIBXML2_WITH_PROGRAMS=OFF -DLIBXML2_WITH_PYTHON=OFF -DLIBXML2_WITH_READLINE=OFF \
        -DLIBXML2_WITH_TESTS=OFF -DLIBXML2_WITH_ZLIB=OFF -DLIBXML2_WITH_THREADS=OFF
    cmake --build "$build/libxml2" --parallel "${JOBS:-8}"
    cmake --install "$build/libxml2"
fi
if [[ "$dependency" == all || "$dependency" == dav1d ]]; then
    reconfigure=()
    if [[ -f "$build/dav1d/build.ninja" ]]; then reconfigure=(--reconfigure); fi
    meson setup "${reconfigure[@]}" "$build/dav1d" "$packages/dav1d" --cross-file "$root/meson/llvmnet.ini" \
        --prefix "$prefix" --libdir lib --buildtype plain --default-library static \
        -Denable_asm=false -Denable_tools=false -Denable_tests=false -Denable_examples=false
    meson compile -C "$build/dav1d" -j "${JOBS:-8}"
    meson install -C "$build/dav1d"
fi