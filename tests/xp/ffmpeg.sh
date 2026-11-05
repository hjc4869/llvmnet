#!/usr/bin/env bash
# Adds ffmpeg's SIMD128 and nasm profiles (SV-07, SV-13) to a cross-platform bundle that tests/xp/build.sh wrote, so that
# the hosts that run it decode with them (SV-20): the SIMD128 profile is portable code on every host; the nasm profile
# calls its kernels from the package's linux-x64 library on Linux x64 and takes its C paths where its x86 detection
# reports nothing (ARM64), and is skipped on x64 hosts the package has no library for. Both decode small HEVC and H.264
# clips with four frame threads and must hash every frame as the native decoder does. The images are translated again
# from the builds' linked bitcode, so that they match the bundle's LlvmNet.Runtime.dll.
#
# Usage: tests/xp/ffmpeg.sh [bundle-dir]   (default artifacts/tests/xp/bundle; rewrites <bundle-dir>.tar.gz)
# Needs the builds of tests/ffmpeg-simd128.sh and tests/ffmpeg-x86asm.sh and ffmpeg with libx264 and libx265.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
bundle="$(realpath -m "${1:-$root/artifacts/tests/xp/bundle}")"
llvmnet="$root/bin/llvmnet"
simd128="$root/artifacts/ffmpeg-browser-portable-simd128"
x86asm="$root/artifacts/ffmpeg-browser-portable-x86asm"
native="$root/artifacts/ffmpeg-browser-system/decode-native"
[[ -f "$bundle/manifest.tsv" ]] || { printf '%s is not a bundle; run tests/xp/build.sh first\n' "$bundle" >&2; exit 2; }
for build in "$simd128" "$x86asm"; do
    [[ -f "$build/decode.bc" ]] || { printf 'No ffmpeg build in %s; run tests/ffmpeg-simd128.sh and tests/ffmpeg-x86asm.sh first\n' "$build" >&2; exit 2; }
done
mkdir -p "$bundle/data"
source='testsrc2=size=320x192:rate=12:duration=2'
ffmpeg -nostdin -v error -f lavfi -i "$source" -c:v libx265 -x265-params 'log-level=error:pools=1' -y "$bundle/data/hevc8.mp4"
ffmpeg -nostdin -v error -f lavfi -i "$source" -pix_fmt yuv420p10le -c:v libx265 -x265-params 'log-level=error:pools=1' -y "$bundle/data/hevc10.mp4"
ffmpeg -nostdin -v error -f lavfi -i "$source" -c:v libx264 -x264-params 'bframes=3:threads=1' -y "$bundle/data/h264.mp4"
sed -i '/^ffmpeg-/d' "$bundle/manifest.tsv"
"$llvmnet" --runtime=portable --simd128 "$simd128/decode.bc" -o "$bundle/images/ffmpeg-simd128.dll"
"$llvmnet" --runtime=portable --simd128 "$simd128/decode.bc" --aot-project "$bundle/aot/ffmpeg-simd128"
"$llvmnet" --runtime=portable --native-kernels "$x86asm/kernels" "$x86asm/decode.bc" -o "$bundle/images/ffmpeg-x86asm.dll"
"$llvmnet" --runtime=portable --native-kernels "$x86asm/kernels" "$x86asm/decode.bc" --aot-project "$bundle/aot/ffmpeg-x86asm"
identifiers="$(python3 -c 'import json, sys; print(",".join(sorted(json.load(open(sys.argv[1]))["runtimes"])))' "$x86asm/kernels/manifest.json")"
count=0
for profile in simd128 x86asm; do
    clips=(hevc8 hevc10)
    requires=-
    if [[ "$profile" == x86asm ]]; then clips+=(h264); requires="kernels:$identifiers"; fi
    for clip in "${clips[@]}"; do
        name="ffmpeg-$profile-$clip"
        # The decoder's frame hashes: the native decoder's where the system build has it, else the image's own C paths,
        # which tests/ffmpeg-x86asm.sh holds to the kernels' output.
        if [[ -x "$native" ]]; then
            "$native" "$bundle/data/$clip.mp4" 1000 video 1 >"$bundle/expected/$name.txt"
        else
            DOTNET_EnableHWIntrinsic=0 dotnet "$bundle/images/ffmpeg-$profile.dll" "$bundle/data/$clip.mp4" 1000 video 1 >"$bundle/expected/$name.txt"
        fi
        grep -q '^frames=24$' "$bundle/expected/$name.txt"
        printf '0\n' >"$bundle/expected/$name.status"
        printf '%s\taot\t{bundle}/data/%s.mp4 1000 video 4 frame\t-\t%s\tffmpeg-%s\n' "$name" "$clip" "$requires" "$profile" >>"$bundle/manifest.tsv"
        count=$((count + 1))
    done
done
tar czf "$bundle.tar.gz" -C "$(dirname "$bundle")" "$(basename "$bundle")"
printf 'Bundle: %d ffmpeg fixtures added to %s (%s)\n' "$count" "$bundle" "$(du -sh "$bundle.tar.gz" | cut -f1)"
