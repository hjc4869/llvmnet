#!/usr/bin/env bash
# tags: integration ffmpeg aot
# requires: ffmpeg-cli nasm lld python3
# serial: 80
# timeout: 3600
# after: xp ffmpeg-simd128 ffmpeg-x86asm
# SV-20: ffmpeg's SIMD128 and nasm profiles join the cross-platform bundle tests/xp.sh built (tests/xp/ffmpeg.sh), so the
# nightly's xp jobs decode with them on Windows x64, macOS ARM64 and Linux ARM64 under the JIT and as NativeAOT: the
# nasm profile's kernels where the package has the host's library and its C paths on ARM64. Here the fixtures run under
# the JIT and as NativeAOT, as tests/xp.sh and tests/xp-aot.sh run the rest; without a bundle newer than the compiler,
# whose LlvmNet.Runtime.dll the images need, the bundle is built first.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
bundle="$root/artifacts/tests/xp/bundle"
if [[ ! "$bundle/manifest.tsv" -nt "$compiler_dll" ]]; then bash "$root/tests/xp/build.sh" "$bundle"; fi
bash "$root/tests/xp/ffmpeg.sh" "$bundle"
names="$(cut -f1 "$bundle/manifest.tsv" | grep '^ffmpeg-' | paste -sd, -)"
bash "$root/tests/xp/run.sh" --aot --only "$names" "$bundle"
count="$(grep -c $'^ffmpeg-[^\t]*\t\\(jit\\|aot\\)\tPASS' "$root"/artifacts/tests/xp/bundle/results/*/results.tsv)"
printf 'PASS: %s ffmpeg SIMD128 and nasm decodes from the cross-platform bundle hash every frame as native, under the JIT and as NativeAOT\n' "$count"
