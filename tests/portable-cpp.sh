#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/portable-cpp"
mkdir -p "$output"
bash "$root/scripts/build-portable-runtime.sh"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
"$root/bin/llvmnet" --runtime=portable -std=c++17 "$root/tests/cpp/portable.cpp" -o "$output/portable.dll"
dotnet "$output/portable.dll"
"$root/bin/llvmnet" --runtime=portable -std=c++17 "$root/tests/cpp/core.cpp" -o "$output/core.dll"
dotnet "$output/core.dll"
printf 'PASS: bundled libc++ vector, array and algorithm under the portable .NET ABI\n'