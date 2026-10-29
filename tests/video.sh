#!/usr/bin/env bash
# tags: integration ffmpeg local-media
# requires: local-media
# serial: 80
# after: codecs
# timeout: 3600
# Opt-in check against a private local video: set FFMPEG_TEST_VIDEO to a file path.
# FFMPEG_TEST_FRAMES selects the frame limit (default 10; use a large value for a full-file comparison).
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
build="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-browser}"
FFMPEG_BUILD_DIR="$build" bash "$root/scripts/validate-video.sh" "$FFMPEG_TEST_VIDEO" "${FFMPEG_TEST_FRAMES:-10}"
dotnet "$checks" inspect "$build/decode.dll"
