#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source_dir="${FFMPEG_SOURCE:-/mnt/storage/david/Development/LightPlayer-Packages/ffmpeg}"
build="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-browser}"
prefix="${LLVMNET_DEPS_PREFIX:-$root/artifacts/deps}"
output="$root/artifacts/tests/ffmpeg-libraries"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
archives=("$build"/libav{codec,device,filter,format,util}/libav*.a "$build"/libsw{scale,resample}/libsw*.a "$prefix/lib/libdav1d.a" "$prefix/lib/libxml2.a" "$prefix/lib/libz.a")
"$root/bin/llvmnet" -I"$build" -I"$source_dir" "$root/tests/ffmpeg/libraries.c" "${archives[@]}" -lm --keep-ir "$output/libraries.bc" -o "$output/libraries.dll"
clang-22 -O1 "$output/libraries.bc" -lm -pthread -o "$output/native"
"$output/native" >"$output/native.txt"
dotnet "$output/libraries.dll" >"$output/cil.txt"
diff -u "$output/native.txt" "$output/cil.txt"
printf 'PASS: swscale, swresample, avfilter allocation and avdevice registration\n'