#!/usr/bin/env bash
# Builds existing libraries with an installed llvmnet toolchain through their own build systems, as a user would, and
# runs what they build (XP-04): zlib 1.3.2 with CMake (its tests through ctest), libxml2 2.15.3 with CMake against that
# zlib (xmllint on a document), and dav1d 1.5.4 with Meson (its command-line tool) when meson is on PATH. Works on Linux
# and macOS; the README's examples are these commands.
#
# Usage: libraries.sh <toolchain-dir> <work-dir> [<sources-dir>]
#   <sources-dir> holds zlib-1.3.2, libxml2-2.15.3 and dav1d-1.5.4 (default: scripts/fetch-sources.sh's copies)
# Needs cmake and ninja (and meson for dav1d) on PATH, and the .NET 10 runtime.
set -euo pipefail
toolchain="$(cd "${1:?usage: libraries.sh <toolchain-dir> <work-dir> [<sources-dir>]}" && pwd)"
work="${2:?usage: libraries.sh <toolchain-dir> <work-dir> [<sources-dir>]}"
mkdir -p "$work"
work="$(cd "$work" && pwd)"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_of() {
    if [[ -n "${3:-}" ]]; then printf '%s/%s-%s' "$3" "$1" "$2"; else bash "$here/../../scripts/fetch-sources.sh" --path "$1"; fi
}
zlib="$(source_of zlib 1.3.2 "${3:-}")"
libxml2="$(source_of libxml2 2.15.3 "${3:-}")"
dav1d="$(source_of dav1d 1.5.4 "${3:-}")"
export PATH="$toolchain/bin:$PATH"
prefix="$work/prefix"
cmake_toolchain="$toolchain/share/llvmnet/cmake/llvmnet.cmake"
rm -rf "$work/zlib" "$work/libxml2" "$work/dav1d" "$prefix"
started=$(date +%s)
step() { printf '== %s (%ss)\n' "$1" "$(( $(date +%s) - started ))"; }

step "zlib: configure, build and test with CMake"
cmake -G Ninja -S "$zlib" -B "$work/zlib" -DCMAKE_TOOLCHAIN_FILE="$cmake_toolchain" -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$prefix" -DZLIB_BUILD_SHARED=OFF -DZLIB_BUILD_TESTING=ON >"$work/zlib.log"
# zlib always builds a -coverage test for Clang, which needs the profiling runtime llvmnet does not have; the example
# tests and minigzip are built and run.
cmake --build "$work/zlib" --target zlibstatic zlib_static_example zlib_static_example64 static_minigzip >>"$work/zlib.log"
(cd "$work/zlib" && ctest --output-on-failure -R 'zlib_static_example') | tail -3
printf 'compressed by llvmnet\n' | "$work/zlib/test/static_minigzip" | "$work/zlib/test/static_minigzip" -d
cmake --install "$work/zlib" >>"$work/zlib.log"

step "libxml2: configure and build with CMake against that zlib"
cmake -G Ninja -S "$libxml2" -B "$work/libxml2" -DCMAKE_TOOLCHAIN_FILE="$cmake_toolchain" -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_PREFIX_PATH="$prefix" -DCMAKE_INSTALL_PREFIX="$prefix" -DBUILD_SHARED_LIBS=OFF \
    -DLIBXML2_WITH_ZLIB=ON -DLIBXML2_WITH_PYTHON=OFF -DLIBXML2_WITH_ICONV=OFF -DLIBXML2_WITH_DOCS=OFF \
    -DLIBXML2_WITH_TESTS=OFF -DLIBXML2_WITH_READLINE=OFF -DLIBXML2_WITH_PROGRAMS=ON >"$work/libxml2.log"
cmake --build "$work/libxml2" >>"$work/libxml2.log"
printf '<?xml version="1.0"?>\n<toolchain name="llvmnet"><host>%s</host><built-by>xmllint</built-by></toolchain>\n' "$(uname -s)" >"$work/sample.xml"
"$work/libxml2/xmllint" --version 2>&1 | head -1
"$work/libxml2/xmllint" --xpath 'string(/toolchain/@name)' "$work/sample.xml"
printf '\n'

if command -v meson >/dev/null 2>&1; then
    step "dav1d: configure and build with Meson"
    meson setup "$work/dav1d" "$dav1d" --cross-file "$toolchain/share/llvmnet/meson/llvmnet.ini" --buildtype release \
        --default-library static -Denable_asm=false -Denable_tools=true -Denable_tests=false >"$work/dav1d.log" 2>&1
    meson compile -C "$work/dav1d" >>"$work/dav1d.log" 2>&1
    "$work/dav1d/tools/dav1d" --version
else
    step "dav1d: skipped (no meson on PATH)"
fi
step "done"
