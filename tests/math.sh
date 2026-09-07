#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/math"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/math.c" -lm -o "$output/native"
"$output/native"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$root/tests/c/math.c" -lm -o "$output/math.dll"
dotnet "$output/math.dll"
printf 'PASS: ffmpeg scalar math probes, rounding and sqrt errno\n'