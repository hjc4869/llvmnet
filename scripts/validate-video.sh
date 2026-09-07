#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-core}"
video="${1:-/home/david/Videos/20260521_215039.mp4}"
frames="${2:-10}"
if [[ ! -f "$build_dir/decode.dll" || ! -x "$build_dir/decode-native" ]]; then
    printf 'Build both decoders with scripts/build-ffmpeg-core.sh first.\n' >&2
    exit 1
fi
"$build_dir/decode-native" "$video" "$frames" >"$build_dir/native-frames.txt"
dotnet "$build_dir/decode.dll" "$video" "$frames" >"$build_dir/cil-frames.txt"
diff -u "$build_dir/native-frames.txt" "$build_dir/cil-frames.txt"
printf 'PASS: native and CIL decoded-frame hashes match (%s frame limit)\n' "$frames"