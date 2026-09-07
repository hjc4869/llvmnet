#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="${FFMPEG_SOURCE:-/mnt/storage/david/Development/LightPlayer-Packages/ffmpeg}"
runtime="${LLVMNET_RUNTIME:-managed-host}"
case "$runtime" in
    managed-host) suffix= ;;
    system|portable) suffix="-$runtime" ;;
    *) printf 'Unsupported LLVMNET_RUNTIME: %s\n' "$runtime" >&2; exit 2 ;;
esac
prefix="${LLVMNET_DEPS_PREFIX:-$root/artifacts/deps$suffix}"
simd128="${FFMPEG_SIMD128:-0}"
simd_flags=()
architecture=(--arch=x86_64 --disable-asm)
case "$simd128" in
    0) ;;
    1) suffix+=-simd128; simd_flags=(--simd128); architecture=(--arch=wasm --enable-asm --enable-simd128) ;;
    *) printf 'FFMPEG_SIMD128 must be 0 or 1\n' >&2; exit 2 ;;
esac
build_dir="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-browser$suffix}"
source "$root/config/ffmpeg-browser.sh"
if [[ -f "$build_dir/llvmnet-simd128" && "$(<"$build_dir/llvmnet-simd128")" != "$simd128" ]]; then
    printf 'Use separate scalar and SIMD128 ffmpeg build directories.\n' >&2
    exit 1
fi
for directory in "$build_dir" "$prefix"; do
    if [[ -f "$directory/llvmnet-runtime" && "$(<"$directory/llvmnet-runtime")" != "$runtime" ]]; then
        printf 'Directory %s belongs to a different ABI; use a separate directory.\n' "$directory" >&2
        exit 1
    fi
done
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
if [[ ! -f "$build_dir/ffbuild/config.mak" || "$root/config/ffmpeg-browser.sh" -nt "$build_dir/ffbuild/config.mak" || "${BASH_SOURCE[0]}" -nt "$build_dir/ffbuild/config.mak" || "$root/config/ffmpeg-configure.patch" -nt "$build_dir/ffbuild/config.mak" ]] ||
    { [[ "$runtime" == portable ]] && [[ -n "$(find "$root/sysroot/dotnet64-v1/include" -type f -newer "$build_dir/ffbuild/config.mak" -print -quit)" ]]; }; then
    configure="$source_dir/configure"
    if [[ "$simd128" == 1 && "$runtime" == portable ]]; then
        mkdir -p "$build_dir/ffbuild"
        if [[ ! -e "$build_dir/src" ]]; then ln -s "$source_dir" "$build_dir/src"; fi
        configure="$build_dir/ffbuild/llvmnet-configure"
        cp "$source_dir/configure" "$configure"
        patch --quiet "$configure" "$root/config/ffmpeg-configure.patch"
    fi
    (
        cd "$build_dir"
        "$configure" \
            --cc="$root/bin/llvmnet" --cxx="$root/bin/llvmnet" --host-cc=clang-22 \
            --ar=llvm-ar-22 --ranlib=llvm-ranlib-22 --nm=llvm-nm-22 \
            --target-os=none "${architecture[@]}" --cpu=generic --enable-cross-compile \
            --enable-static --disable-shared --disable-programs --disable-debug --disable-doc \
            --disable-inline-asm --disable-x86asm --disable-hwaccels \
            --disable-autodetect --disable-runtime-cpudetect --disable-network \
            --enable-pthreads --disable-w32threads --disable-os2threads \
            --disable-filters --disable-devices --disable-protocols --enable-protocol=file \
            --disable-bsfs --disable-muxers --disable-demuxers --disable-parsers \
            --disable-decoders --disable-encoders --enable-swscale --enable-avfilter --enable-avdevice \
            --pkg-config-flags=--static --enable-zlib --enable-libxml2 --enable-libdav1d \
                --disable-stripping --extra-cflags="--runtime=$runtime ${simd_flags[*]} -O1 -fno-vectorize -fno-slp-vectorize -I$prefix/include" \
                --extra-ldflags="--runtime=$runtime ${simd_flags[*]} -L$prefix/lib" "${ffmpeg_components[@]}"
    )
            printf '%s\n' "$runtime" >"$build_dir/llvmnet-runtime"
            printf '%s\n' "$simd128" >"$build_dir/llvmnet-simd128"
fi
for feature in CONFIG_ZLIB CONFIG_LIBXML2 HAVE_PTHREADS; do
    grep -q "^#define $feature 1$" "$build_dir/config.h"
done
grep -q '^#define CONFIG_LIBDAV1D_DECODER 1$' "$build_dir/config_components.h"
grep -q "^#define HAVE_SIMD128 $simd128$" "$build_dir/config.h"
if [[ "$simd128" == 1 ]]; then grep -q '^#define ARCH_WASM 1$' "$build_dir/config.h"; fi
for feature in HAVE_INLINE_ASM CONFIG_NETWORK; do
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
"$root/bin/llvmnet" --runtime="$runtime" "${simd_flags[@]}" -pthread -O1 -I"$build_dir" -I"$source_dir" "$root/tests/ffmpeg/decode.c" \
    "${archives[@]}" -lm --keep-ir "$build_dir/decode.bc" -o "$build_dir/decode.dll"
if [[ "$runtime" != portable && "$simd128" == 0 ]]; then
    clang-22 -O1 "$build_dir/decode.bc" -lm -pthread -o "$build_dir/decode-native"
fi
printf 'Built browser-profile threaded ffmpeg decoder libraries (%s ABI, SIMD128=%s) in %s\n' "$runtime" "$simd128" "$build_dir"