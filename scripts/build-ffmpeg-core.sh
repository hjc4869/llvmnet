#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="${FFMPEG_SOURCE:-/mnt/storage/david/Development/LightPlayer-Packages/ffmpeg}"
build_dir="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-core}"
if [[ ! -x "$source_dir/configure" ]]; then
    printf 'FFmpeg source not found: %s\n' "$source_dir" >&2
    exit 1
fi
if [[ -f "$source_dir/ffbuild/config.mak" ]]; then
    printf 'FFmpeg source is configured in-tree; use a clean source checkout. This script will not modify it.\n' >&2
    exit 1
fi
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
mkdir -p "$build_dir"
if [[ ! -f "$build_dir/ffbuild/config.mak" || "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" -nt "$build_dir/ffbuild/config.mak" ]]; then
    (
        cd "$build_dir"
        "$source_dir/configure" \
            --cc="$root/bin/llvmnet" \
            --cxx="$root/bin/llvmnet" \
            --host-cc=clang-22 \
            --ar=llvm-ar-22 --ranlib=llvm-ranlib-22 --nm=llvm-nm-22 \
            --target-os=none --arch=x86_64 --cpu=generic \
            --enable-cross-compile --enable-static --disable-shared \
            --disable-asm --disable-inline-asm --disable-x86asm \
            --disable-runtime-cpudetect --disable-autodetect \
            --disable-pthreads --disable-w32threads --disable-os2threads \
            --disable-hwaccels --disable-network --disable-programs \
            --disable-debug --disable-doc --disable-stripping \
            --disable-everything --disable-avdevice --disable-avfilter \
            --enable-avcodec --enable-avformat --enable-avutil \
            --enable-decoder=hevc,h264,aac \
            --enable-parser=hevc,h264,aac \
            --enable-demuxer=mov --enable-protocol=file \
            --extra-cflags='-O1 -fno-vectorize -fno-slp-vectorize'
    )
fi
if [[ "${1:-}" == "--configure-only" ]]; then
    exit 0
fi
make -C "$build_dir" -j"${JOBS:-8}"
if [[ "${1:-}" == "--objects-only" ]]; then
    exit 0
fi
archives=("$build_dir/libavformat/libavformat.a" "$build_dir/libavcodec/libavcodec.a" "$build_dir/libavutil/libavutil.a" "$build_dir/libswresample/libswresample.a")
"$root/bin/llvmnet" -O1 -I"$build_dir" -I"$source_dir" "$root/tests/ffmpeg/decode.c" \
    "${archives[@]}" -lm --keep-ir "$build_dir/decode.bc" -o "$build_dir/decode.dll"
clang-22 -O1 "$build_dir/decode.bc" -lm -o "$build_dir/decode-native"
printf 'Built CIL and native software decoders in %s\n' "$build_dir"