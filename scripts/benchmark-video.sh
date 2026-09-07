#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-browser}"
video="${1:-/home/david/Videos/20260521_215039.mp4}"
frames="${2:-1000000}"
output="$build/video-validation"
if [[ ! -x /usr/bin/time || ! -f "$build/decode.dll" || ! -x "$build/decode-native" ]]; then
    printf 'Build ffmpeg first and ensure /usr/bin/time is installed.\n' >&2
    exit 1
fi
mkdir -p "$output"
format='{"elapsed_seconds":%e,"user_seconds":%U,"system_seconds":%S,"max_rss_kib":%M}'
/usr/bin/time -f "$format" -o "$output/native-time.json" \
    "$build/decode-native" "$video" "$frames" >"$output/native-frames.txt"
/usr/bin/time -f "$format" -o "$output/cil-time.json" \
    dotnet "$build/decode.dll" "$video" "$frames" >"$output/cil-frames.txt"
diff -u "$output/native-frames.txt" "$output/cil-frames.txt"
printf 'PASS: all requested decoded-frame hashes match. End-to-end timings include process startup, JIT, I/O and hashing.\n'
printf 'Native: '
cat "$output/native-time.json"
printf 'CIL: '
cat "$output/cil-time.json"
tail -n 1 "$output/cil-frames.txt"