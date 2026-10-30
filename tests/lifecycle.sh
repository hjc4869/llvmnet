#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/lifecycle"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/lifecycle.c" -o "$output/native"
"$output/native" >"$output/native.txt"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" "$root/tests/c/lifecycle.c" -o "$output/lifecycle-$runtime.dll"
    dotnet "$output/lifecycle-$runtime.dll" >"$output/cil-$runtime.txt"
    diff -u "$output/native.txt" "$output/cil-$runtime.txt"
done
printf 'PASS: global constructor/destructor ordering and cryptographic entropy (system and portable)\n'
