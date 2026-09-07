#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="${FFMPEG_SOURCE:-/mnt/storage/david/Development/LightPlayer-Packages/ffmpeg}"
build_dir="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-browser}"
prefix="${LLVMNET_DEPS_PREFIX:-$root/artifacts/deps}"
source "$root/config/ffmpeg-browser.sh"
if [[ -f "$source_dir/ffbuild/config.mak" ]]; then
    printf 'Use a clean FFmpeg source checkout; this script does not modify source files.\n' >&2
    exit 1
fi
for archive in libdav1d.a libxml2.a libz.a; do
    if [[ ! -f "$prefix/lib/$archive" ]]; then
        printf 'Missing %s; run scripts/build-dependencies.sh first.\n' "$archive" >&2
        exit 1
    fi
done
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
mkdir -p "$build_dir"
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
if [[ ! -f "$build_dir/ffbuild/config.mak" || "$root/config/ffmpeg-browser.sh" -nt "$build_dir/ffbuild/config.mak" ]]; then
    (
        cd "$build_dir"
        "$source_dir/configure" \
            --cc="$root/bin/llvmnet" --cxx="$root/bin/llvmnet" --host-cc=clang-22 \
            --ar=llvm-ar-22 --ranlib=llvm-ranlib-22 --nm=llvm-nm-22 \
            --target-os=none --arch=x86_64 --cpu=generic --enable-cross-compile \
            --enable-static --disable-shared --disable-programs --disable-debug --disable-doc \
            --disable-asm --disable-inline-asm --disable-x86asm --disable-hwaccels \
            --disable-autodetect --disable-runtime-cpudetect --disable-network \
            --disable-pthreads --disable-w32threads --disable-os2threads \
            --disable-filters --disable-devices --disable-protocols --enable-protocol=file \
            --disable-bsfs --disable-muxers --disable-demuxers --disable-parsers \
            --disable-decoders --disable-encoders --enable-swscale --enable-avfilter --enable-avdevice \
            --pkg-config-flags=--static --enable-zlib --enable-libxml2 --enable-libdav1d \
            --disable-stripping --extra-cflags="-O1 -fno-vectorize -fno-slp-vectorize -I$prefix/include" \
            --extra-ldflags="-L$prefix/lib" "${ffmpeg_components[@]}"
    )
fi
for feature in CONFIG_ZLIB CONFIG_LIBXML2; do
    grep -q "^#define $feature 1$" "$build_dir/config.h"
done
grep -q '^#define CONFIG_LIBDAV1D_DECODER 1$' "$build_dir/config_components.h"
for feature in HAVE_PTHREADS HAVE_INLINE_ASM CONFIG_NETWORK; do
    grep -q "^#define $feature 0$" "$build_dir/config.h"
done
if [[ "${1:-}" == --configure-only ]]; then exit 0; fi
make -C "$build_dir" -j"${JOBS:-8}"
if [[ "${1:-}" == --objects-only ]]; then exit 0; fi
archives=(
    "$build_dir/libavformat/libavformat.a" "$build_dir/libavcodec/libavcodec.a"
    "$build_dir/libavdevice/libavdevice.a" "$build_dir/libavfilter/libavfilter.a"
    "$build_dir/libavutil/libavutil.a" "$build_dir/libswresample/libswresample.a" "$build_dir/libswscale/libswscale.a"
    "$prefix/lib/libdav1d.a" "$prefix/lib/libxml2.a" "$prefix/lib/libz.a"
)
"$root/bin/llvmnet" -O1 -I"$build_dir" -I"$source_dir" "$root/tests/ffmpeg/decode.c" \
    "${archives[@]}" -lm --keep-ir "$build_dir/decode.bc" -o "$build_dir/decode.dll"
clang-22 -O1 "$build_dir/decode.bc" -lm -pthread -o "$build_dir/decode-native"
printf 'Built browser-equivalent single-threaded ffmpeg decoder libraries in %s\n' "$build_dir"