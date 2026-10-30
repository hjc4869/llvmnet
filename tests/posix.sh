#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/posix"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/posix.c" -o "$output/native"
"$output/native" "$output/native-directory" >"$output/native.txt"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" "$root/tests/c/posix.c" -o "$output/posix-$runtime.dll"
    dotnet "$output/posix-$runtime.dll" "$output/cil-$runtime-directory" >"$output/cil-$runtime.txt"
    diff -u "$output/native.txt" "$output/cil-$runtime.txt"
done
printf 'PASS: managed mmap, directory layout, permissions, errno and signal callbacks (system and portable)\n'
