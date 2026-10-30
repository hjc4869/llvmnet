#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-core}"
video="${1:-${FFMPEG_TEST_VIDEO:?Pass a video file or set FFMPEG_TEST_VIDEO}}"
frames="${2:-10}"
# A portable build has no native decoder of its own; FFMPEG_NATIVE_DECODER names the oracle, such as the system build's.
native="${FFMPEG_NATIVE_DECODER:-$build_dir/decode-native}"
if [[ ! -f "$build_dir/decode.dll" || ! -x "$native" ]]; then
    printf 'Build the CIL decoder and a native decoder (scripts/build-ffmpeg.sh) first.\n' >&2
    exit 1
fi
"$native" "$video" "$frames" >"$build_dir/native-frames.txt"
dotnet "$build_dir/decode.dll" "$video" "$frames" >"$build_dir/cil-frames.txt"
diff -u "$build_dir/native-frames.txt" "$build_dir/cil-frames.txt"
printf 'PASS: native and CIL decoded-frame hashes match (%s frame limit)\n' "$frames"