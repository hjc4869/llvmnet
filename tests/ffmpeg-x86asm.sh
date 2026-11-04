#!/usr/bin/env bash
# tags: integration ffmpeg aot
# requires: ffmpeg-cli nasm lld python3
# serial: 75
# timeout: 3600
# ffmpeg's browser profile with its nasm kernels in a native kernel package (FFMPEG_X86ASM=1 in scripts/build-ffmpeg.sh,
# SV-13). The same image decodes HEVC 8-bit, HEVC 10-bit and H.264 through its kernels under the JIT, with AVX off
# (DOTNET_EnableAVX=0, so ffmpeg selects its SSE kernels) and as NativeAOT; each must hash every frame as the image does
# through its C paths, which it takes when CPUID reports nothing (DOTNET_EnableHWIntrinsic=0, as on an ARM64 host).
# Without the package's library the C paths still decode, and a kernel call ends the process with llvmnet's message.
# The system-mode profile links the same kernels into the image's native library (SV-15) and must hash as well.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/ffmpeg-x86asm"
build="$root/artifacts/ffmpeg-browser-portable-x86asm"
system="$root/artifacts/ffmpeg-browser-system-x86asm"
rm -rf "$output"
mkdir -p "$output"
if [[ "${FFMPEG_X86ASM_SKIP_BUILD:-0}" != 1 ]]; then
    for runtime in portable system; do
        LLVMNET_RUNTIME="$runtime" bash "$root/scripts/build-dependencies.sh" >"$output/deps-$runtime.log" 2>&1
    done
    LLVMNET_RUNTIME=portable FFMPEG_X86ASM=1 FFMPEG_BUILD_DIR="$build" bash "$root/scripts/build-ffmpeg.sh" >"$output/build.log" 2>&1
    LLVMNET_RUNTIME=system FFMPEG_X86ASM=1 FFMPEG_BUILD_DIR="$system" bash "$root/scripts/build-ffmpeg.sh" >"$output/build-system.log" 2>&1
fi
grep -q '^#define HAVE_X86ASM 1$' "$build/config.h"
dotnet "$checks" inspect "$build/decode.dll" >"$output/inspect.txt"
grep -q '^llvmnet.kernels: ffmpeg-x86/.* (linux-x64)' "$output/inspect.txt"
"$llvmnet" --runtime=portable --nativeaot --native-kernels "$build/kernels" "$build/decode.bc" -o "$output/decode-aot" >"$output/aot.log" 2>&1
if ldd "$output/decode-aot" | grep -q ffmpeg-x86-kernels; then printf 'FAIL: the NativeAOT decoder loads the kernel library instead of linking it\n' >&2; exit 1; fi

source='testsrc2=size=320x192:rate=12:duration=2'
ffmpeg -nostdin -v error -f lavfi -i "$source" -c:v libx265 -x265-params 'log-level=error:pools=1' -y "$output/hevc8.mp4"
ffmpeg -nostdin -v error -f lavfi -i "$source" -pix_fmt yuv420p10le -c:v libx265 -x265-params 'log-level=error:pools=1' -y "$output/hevc10.mp4"
ffmpeg -nostdin -v error -f lavfi -i "$source" -c:v libx264 -x264-params 'bframes=3:threads=1' -y "$output/h264.mp4"
for clip in hevc8 hevc10 h264; do
    fixture="$output/$clip.mp4"
    DOTNET_EnableHWIntrinsic=0 dotnet "$build/decode.dll" "$fixture" 1000 video 1 >"$output/$clip-c.txt" 2>/dev/null
    grep -q '^frames=24$' "$output/$clip-c.txt"
    dotnet "$build/decode.dll" "$fixture" 1000 video 4 frame >"$output/$clip-jit.txt" 2>/dev/null
    DOTNET_EnableAVX=0 dotnet "$build/decode.dll" "$fixture" 1000 video 1 >"$output/$clip-sse.txt" 2>/dev/null
    "$output/decode-aot" "$fixture" 1000 video 4 slice >"$output/$clip-aot.txt" 2>/dev/null
    for engine in jit sse aot; do diff -u "$output/$clip-c.txt" "$output/$clip-$engine.txt"; done
    printf 'PASS: %s decodes through the kernels as through the C paths (JIT, SSE only, NativeAOT)\n' "$clip"
done

# Without this host's library: the C paths where CPUID reports nothing, and the fail-fast message where it reports AVX2.
mv "$build/runtimes/linux-x64" "$output/hidden"
trap 'mv "$output/hidden" "$build/runtimes/linux-x64"' EXIT
DOTNET_EnableHWIntrinsic=0 dotnet "$build/decode.dll" "$output/hevc8.mp4" 1000 video 1 2>/dev/null | diff -u "$output/hevc8-c.txt" -
code=0
dotnet "$build/decode.dll" "$output/hevc8.mp4" 1000 video 1 >/dev/null 2>"$output/missing.err" || code=$?
test "$code" -eq 127
grep -q '^llvmnet: native kernel ff_[a-z0-9_]* of package ffmpeg-x86 was called, but its linux-x64 library .*libffmpeg-x86-kernels.so does not load' "$output/missing.err"
printf 'PASS: without the library the C paths decode, and a kernel call stops the process\n'

# System mode: the kernels in the image's native library, under the JIT and as NativeAOT, which links the archives' native
# members again from the command line.
trap - EXIT
mv "$output/hidden" "$build/runtimes/linux-x64"
"$llvmnet" --runtime=system --nativeaot "$system/decode.bc" "$system/libavcodec/libavcodec.a" "$system/libavutil/libavutil.a" \
    "$system/libswscale/libswscale.a" "$system/libswresample/libswresample.a" -o "$output/decode-system-aot" >"$output/system-aot.log" 2>&1
for clip in hevc8 hevc10 h264; do
    dotnet "$system/decode.dll" "$output/$clip.mp4" 1000 video 4 frame 2>/dev/null | diff -u "$output/$clip-c.txt" -
    "$output/decode-system-aot" "$output/$clip.mp4" 1000 video 4 slice 2>/dev/null | diff -u "$output/$clip-c.txt" -
done
printf 'PASS: the system-mode profile decodes through its linked kernels (JIT, NativeAOT)\n'
