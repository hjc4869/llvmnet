#!/usr/bin/env bash
# tags: integration ffmpeg
# serial: 50
# after: dependencies codecs
# timeout: 1800
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
source_dir="${FFMPEG_SOURCE:-$("$root/scripts/fetch-sources.sh" --path ffmpeg)}"
output="$root/artifacts/tests/ffmpeg-libraries"
mkdir -p "$output"
for runtime in system portable; do
    build="$root/artifacts/ffmpeg-browser-$runtime"
    prefix="$root/artifacts/deps-$runtime"
    archives=("$build"/libav{codec,device,filter,format,util}/libav*.a "$build"/libsw{scale,resample}/libsw*.a "$prefix/lib/libdav1d.a" "$prefix/lib/libxml2.a" "$prefix/lib/libz.a")
    "$llvmnet" --runtime="$runtime" -I"$build" -I"$source_dir" "$root/tests/ffmpeg/libraries.c" "${archives[@]}" -lm \
        --keep-ir "$output/libraries-$runtime.bc" -o "$output/libraries-$runtime.dll"
    if [[ "$runtime" == system ]]; then
        clang-22 -O1 "$output/libraries-system.bc" -lm -pthread -o "$output/native"
        "$output/native" >"$output/native.txt"
    fi
    dotnet "$output/libraries-$runtime.dll" >"$output/$runtime.txt"
    diff -u "$output/native.txt" "$output/$runtime.txt"
done
printf 'PASS: swscale, swresample, avfilter allocation and avdevice registration in system and portable mode\n'
