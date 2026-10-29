#!/usr/bin/env bash
# tags: toolchain cpp
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/portable-cpp"
mkdir -p "$output"
"$root/bin/llvmnet" --runtime=portable -std=c++17 "$root/tests/cpp/portable.cpp" -o "$output/portable.dll"
dotnet "$output/portable.dll"
"$root/bin/llvmnet" --runtime=portable -std=c++17 "$root/tests/cpp/core.cpp" -o "$output/core.dll"
dotnet "$output/core.dll"
printf 'PASS: bundled libc++ vector, array and algorithm under the portable .NET ABI\n'