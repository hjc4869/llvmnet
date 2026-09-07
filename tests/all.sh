#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
mode="${1:-core}"
case "$mode" in core|integration|spec-test) ;; *) printf 'Usage: %s [core|integration|spec-test]\n' "$0" >&2; exit 2 ;; esac
dotnet build "$root/llvmnet.slnx" -c Release --nologo
for test in smoke link runtime math io time float80 ir lifecycle ctype atomics threads posix cpp fortran wide vectors spec-matrix; do
    bash "$root/tests/$test.sh"
done
if [[ "$mode" == integration || "$mode" == spec-test ]]; then
    bash "$root/scripts/build-dependencies.sh"
    bash "$root/tests/dependencies.sh" zlib libxml2 dav1d
    bash "$root/tests/codecs.sh"
    bash "$root/tests/codecs-extended.sh"
    bash "$root/tests/manifests.sh"
    bash "$root/tests/ffmpeg-libraries.sh"
    FFMPEG_BUILD_DIR="$root/artifacts/ffmpeg-browser" bash "$root/scripts/validate-video.sh" "${FFMPEG_TEST_VIDEO:-/home/david/Videos/20260521_215039.mp4}" 10
    dotnet "$root/tests/LlvmNet.Checks/bin/Release/net10.0/LlvmNet.Checks.dll" inspect "$root/artifacts/ffmpeg-browser/decode.dll"
fi
if [[ "$mode" == spec-test ]]; then
    bash "$root/scripts/benchmark-video.sh" "${FFMPEG_TEST_VIDEO:-/home/david/Videos/20260521_215039.mp4}"
    bash "$root/scripts/spec-mcf.sh" test
    bash "$root/scripts/spec-deepsjeng.sh" test
    bash "$root/scripts/spec-exchange2.sh" test
    bash "$root/scripts/spec-zstd.sh"
    bash "$root/scripts/spec-lbm.sh" 2017 test
    bash "$root/scripts/spec-lbm.sh" 2026 test
fi
printf 'PASS: llvmnet %s checks\n' "$mode"