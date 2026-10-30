#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="${LLVMNET_RUNTIME:-portable}"
case "$runtime" in
    system|portable) suffix="-$runtime" ;;
    *) printf 'Unsupported LLVMNET_RUNTIME: %s (portable or system)\n' "$runtime" >&2; exit 2 ;;
esac
prefix="${LLVMNET_DEPS_PREFIX:-$root/artifacts/deps$suffix}"
build="${LLVMNET_DEPS_BUILD_DIR:-$root/artifacts/deps-build$suffix}"
for directory in "$build" "$prefix"; do
    if [[ -f "$directory/llvmnet-runtime" && "$(<"$directory/llvmnet-runtime")" != "$runtime" ]]; then
        printf 'Dependency directory %s belongs to a different ABI; use a separate directory.\n' "$directory" >&2
        exit 1
    fi
done
export PATH="$root/bin:$PATH"
bash "$root/scripts/ensure-built.sh"
# Objects built against another portable sysroot carry an incompatible ABI; start over when it changes.
abi_stamp="$runtime"
[[ "$runtime" == portable ]] && abi_stamp+=" $(cat "$root/sysroot/dotnet64-v2/.llvmnet-inputs")"
if [[ "$(cat "$build/llvmnet-abi" 2>/dev/null)" != "$abi_stamp" ]]; then rm -rf "${build:?}" "${prefix:?}"; fi
mkdir -p "$build" "$prefix"
printf '%s\n' "$abi_stamp" >"$build/llvmnet-abi"
printf '%s\n' "$runtime" | tee "$build/llvmnet-runtime" >"$prefix/llvmnet-runtime"
cflags="--runtime=$runtime -O1 -fno-vectorize -fno-slp-vectorize -fno-stack-protector"
dependency="${1:-all}"
# A build tree configured from another source directory must be recreated, not reused.
fresh_tree() {
    local tree="$1" source="$2"
    if [[ -d "$tree" && "$(cat "$tree.source" 2>/dev/null)" != "$source" ]]; then rm -rf "$tree"; fi
    mkdir -p "$(dirname "$tree")"
    printf '%s\n' "$source" >"$tree.source"
}
if [[ "$dependency" == all || "$dependency" == zlib ]]; then
    zlib_source="${ZLIB_SOURCE:-$("$root/scripts/fetch-sources.sh" --path zlib)}"
    fresh_tree "$build/zlib" "$zlib_source"
    cmake -G Ninja -S "$zlib_source" -B "$build/zlib" \
        -DCMAKE_TOOLCHAIN_FILE="$root/cmake/llvmnet.cmake" \
        -DCMAKE_C_FLAGS="$cflags" \
        -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_FLAGS_RELEASE='-O1 -DNDEBUG' \
        -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib \
        -DZLIB_BUILD_SHARED=OFF -DZLIB_BUILD_STATIC=ON -DZLIB_BUILD_TESTING=OFF
    cmake --build "$build/zlib" --parallel "${JOBS:-8}"
    cmake --install "$build/zlib"
fi
if [[ "$dependency" == all || "$dependency" == libxml2 ]]; then
    libxml2_source="${LIBXML2_SOURCE:-$("$root/scripts/fetch-sources.sh" --path libxml2)}"
    fresh_tree "$build/libxml2" "$libxml2_source"
    cmake -G Ninja -S "$libxml2_source" -B "$build/libxml2" \
        -DCMAKE_TOOLCHAIN_FILE="$root/cmake/llvmnet.cmake" \
        -DCMAKE_C_FLAGS="$cflags" \
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
    dav1d_source="${DAV1D_SOURCE:-$("$root/scripts/fetch-sources.sh" --path dav1d)}"
    fresh_tree "$build/dav1d" "$dav1d_source"
    reconfigure=()
    if [[ -f "$build/dav1d/build.ninja" ]]; then reconfigure=(--reconfigure); fi
    meson setup "${reconfigure[@]}" "$build/dav1d" "$dav1d_source" --cross-file "$root/meson/llvmnet.ini" \
        --prefix "$prefix" --libdir lib --buildtype plain --default-library static \
        -Dc_args="['--runtime=$runtime', '-O1', '-fno-vectorize', '-fno-slp-vectorize', '-fno-stack-protector']" \
        -Dc_link_args="['--runtime=$runtime']" \
        -Denable_asm=false -Denable_tools=false -Denable_tests=false -Denable_examples=false
    meson compile -C "$build/dav1d" -j "${JOBS:-8}"
    meson install -C "$build/dav1d"
fi