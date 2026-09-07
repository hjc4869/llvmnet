#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
build="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-browser}"
fixtures="$root/artifacts/tests/manifests"
mkdir -p "$fixtures/dash" "$fixtures/hls"
if [[ ! -f "$fixtures/source.mp4" ]]; then
    ffmpeg -nostdin -v error -f lavfi -i 'testsrc2=size=128x72:rate=5:duration=1.2' \
        -c:v libx264 -threads 1 -g 2 -pix_fmt yuv420p -y "$fixtures/source.mp4"
fi
if [[ ! -f "$fixtures/dash/manifest.mpd" ]]; then
    ffmpeg -nostdin -v error -i "$fixtures/source.mp4" -map 0:v:0 -c copy \
        -f dash -seg_duration 0.4 -y "$fixtures/dash/manifest.mpd"
fi
if [[ ! -f "$fixtures/hls/playlist.m3u8" ]]; then
    ffmpeg -nostdin -v error -i "$fixtures/source.mp4" -map 0:v:0 -c copy \
        -f hls -hls_time 0.4 -hls_list_size 0 -hls_segment_filename "$fixtures/hls/segment%03d.ts" -y "$fixtures/hls/playlist.m3u8"
fi
for file in dash/manifest.mpd hls/playlist.m3u8; do
    "$build/decode-native" "$fixtures/$file" 100 >"$fixtures/native.txt"
    dotnet "$build/decode.dll" "$fixtures/$file" 100 >"$fixtures/cil.txt"
    diff -u "$fixtures/native.txt" "$fixtures/cil.txt"
    printf 'PASS: local-file %s decoded hashes match native\n' "$file"
done