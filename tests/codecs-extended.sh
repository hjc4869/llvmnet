#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
build="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-browser}"
fixtures="$root/artifacts/tests/codecs-extended"
mkdir -p "$fixtures"
video_source='testsrc2=size=128x96:rate=25:duration=0.16'
audio_source='sine=frequency=997:sample_rate=48000:duration=0.15'
video_cases=(mpeg1video:mpeg1.mpg mpeg2video:mpeg2.mpg mpeg4:mpeg4.avi msmpeg4v2:msmpeg4v2.avi msmpeg4:msmpeg4.avi wmv1:wmv1.asf wmv2:wmv2.asf libvpx:vp8.webm libtheora:theora.ogv prores_ks:prores.mov mjpeg:mjpeg.avi)
audio_cases=(alac:alac.m4a ac3:ac3.ac3 eac3:eac3.eac3 mp2:mp2.mp2 libmp3lame:mp3.mp3 libopus:opus.ogg libvorbis:vorbis.ogg pcm_f32le:float.wav pcm_s24le:pcm24.wav pcm_alaw:alaw.wav pcm_mulaw:mulaw.wav)
check() {
    local file="$1" mode="$2"
    "$build/decode-native" "$fixtures/$file" 100 "$mode" >"$fixtures/$file-native.txt"
    dotnet "$build/decode.dll" "$fixtures/$file" 100 "$mode" >"$fixtures/$file-cil.txt"
    diff -u "$fixtures/$file-native.txt" "$fixtures/$file-cil.txt"
    printf 'PASS: decoded %s hashes match native\n' "$file"
}
for item in "${video_cases[@]}"; do
    encoder="${item%%:*}"; file="${item#*:}"
    if [[ ! -f "$fixtures/$file" ]]; then
        ffmpeg -nostdin -v error -f lavfi -i "$video_source" -c:v "$encoder" -threads 1 -y "$fixtures/$file"
    fi
    check "$file" video
done
for item in tiff:image.tiff libwebp:image.webp; do
    encoder="${item%%:*}"; file="${item#*:}"
    if [[ ! -f "$fixtures/$file" ]]; then
        ffmpeg -nostdin -v error -f lavfi -i "$video_source" -frames:v 1 -c:v "$encoder" -threads 1 -y "$fixtures/$file"
    fi
    check "$file" video
done
for item in "${audio_cases[@]}"; do
    encoder="${item%%:*}"; file="${item#*:}"
    if [[ ! -f "$fixtures/$file" ]]; then
        ffmpeg -nostdin -v error -f lavfi -i "$audio_source" -c:a "$encoder" -threads 1 -y "$fixtures/$file"
    fi
    check "$file" audio
done