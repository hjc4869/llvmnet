#!/usr/bin/env bash
# Runs a named test gate (default: core); tests/run.sh selects tests directly.
#   core         -> tests/run.sh --tag core
#   integration  -> tests/run.sh --tag core --tag integration
#   spec-test    -> integration, then the licensed SPEC development runners, and the video benchmark when
#                   FFMPEG_TEST_VIDEO is set
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
mode="${1:-core}"
case "$mode" in
    core) exec bash "$root/tests/run.sh" --tag core ;;
    integration) exec bash "$root/tests/run.sh" --tag core --tag integration ;;
    spec-test) ;;
    *) printf 'Usage: %s [core|integration|spec-test]\n' "$0" >&2; exit 2 ;;
esac
bash "$root/tests/run.sh" --tag core --tag integration
export LLVMNET_SKIP_BUILD=1
if [[ -n "${FFMPEG_TEST_VIDEO:-}" ]]; then
    FFMPEG_BUILD_DIR="$root/artifacts/ffmpeg-browser" bash "$root/scripts/benchmark-video.sh" "$FFMPEG_TEST_VIDEO"
fi
bash "$root/scripts/spec-mcf.sh" test
bash "$root/scripts/spec-deepsjeng.sh" test
bash "$root/scripts/spec-exchange2.sh" test
bash "$root/scripts/spec-zstd.sh"
bash "$root/scripts/spec-lbm.sh" 2017 test
bash "$root/scripts/spec-lbm.sh" 2026 test
printf 'PASS: llvmnet spec-test checks\n'
