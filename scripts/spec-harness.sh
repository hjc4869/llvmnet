#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
suite="${1:?Usage: spec-harness.sh installed-suite toolchain-prefix [runtime] [benchmark] [size]}"
toolchain="${2:?Specify the installed toolchain prefix}"
runtime="${3:-system}"
benchmark="${4:-505.mcf_r}"
size="${5:-test}"
suite="$(realpath "$suite")"
toolchain="$(realpath "$toolchain")"
if [[ ! -x "$suite/bin/runcpu" ]]; then printf 'Install the SPEC kit first: %s\n' "$suite" >&2; exit 1; fi
if [[ ! -f "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" ]]; then
    printf 'Complete the full ffmpeg video validation before running SPEC.\n' >&2
    exit 1
fi
diff -q "$root/artifacts/ffmpeg-browser/video-validation/native-frames.txt" "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" >/dev/null
mkdir -p "$suite/config/flags"
cp "$root/config/spec/llvmnet.cfg" "$suite/config/llvmnet.cfg"
cp "$root/config/spec/llvmnet.xml" "$suite/config/flags/llvmnet.xml"
cd "$suite"
set +u
source ./shrc
set -u
runcpu --config=llvmnet --define "llvmnet_dir=$toolchain" --define "llvmnet_runtime=$runtime" \
    --size="$size" --iterations=1 --copies=1 --noreportable --tune=base --action=run "$benchmark"