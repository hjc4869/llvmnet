#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
build="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-browser}"
fixtures="$root/artifacts/tests/codecs"
mkdir -p "$fixtures"
FFMPEG_BUILD_DIR="$build" bash "$root/scripts/build-ffmpeg.sh" >"$fixtures/build.log" 2>&1
video_source='testsrc2=size=128x72:rate=5:duration=0.8'
audio_source='sine=frequency=997:sample_rate=48000:duration=0.15'
if [[ ! -f "$fixtures/h264.mp4" ]]; then
    ffmpeg -nostdin -v error -f lavfi -i "$video_source" -c:v libx264 -threads 1 -pix_fmt yuv420p -y "$fixtures/h264.mp4"
    ffmpeg -nostdin -v error -f lavfi -i "$video_source" -c:v libvpx-vp9 -threads 1 -cpu-used 8 -y "$fixtures/vp9.webm"
    ffmpeg -nostdin -v error -f lavfi -i "$video_source" -c:v libaom-av1 -threads 1 -cpu-used 8 -row-mt 0 -y "$fixtures/av1.mkv"
    ffmpeg -nostdin -v error -f lavfi -i "$video_source" -frames:v 1 -c:v png -threads 1 -update 1 -y "$fixtures/image.png"
    ffmpeg -nostdin -v error -f lavfi -i "$audio_source" -c:a pcm_s16le -threads 1 -y "$fixtures/pcm.wav"
    ffmpeg -nostdin -v error -f lavfi -i "$audio_source" -c:a flac -threads 1 -y "$fixtures/audio.flac"
    ffmpeg -nostdin -v error -f lavfi -i "$audio_source" -c:a aac -threads 1 -y "$fixtures/aac.m4a"
fi
for fixture in h264.mp4 vp9.webm av1.mkv image.png pcm.wav audio.flac aac.m4a; do
    mode=video
    case "$fixture" in *.wav|*.flac|*.m4a) mode=audio ;; esac
    "$build/decode-native" "$fixtures/$fixture" 100 "$mode" >"$fixtures/$fixture-native.txt"
    dotnet "$build/decode.dll" "$fixtures/$fixture" 100 "$mode" >"$fixtures/$fixture-cil.txt"
    diff -u "$fixtures/$fixture-native.txt" "$fixtures/$fixture-cil.txt"
    printf 'PASS: decoded %s hashes match native\n' "$fixture"
done