#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/posix"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 "$root/tests/c/posix.c" -o "$output/native"
"$output/native" "$output/native-directory" >"$output/native.txt"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$root/tests/c/posix.c" -o "$output/posix.dll"
dotnet "$output/posix.dll" "$output/cil-directory" >"$output/cil.txt"
diff -u "$output/native.txt" "$output/cil.txt"
printf 'PASS: managed mmap, directory layout, permissions, errno and signal callbacks\n'