#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/ctype"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/ctype.c" -o "$output/native"
"$output/native"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" "$root/tests/c/ctype.c" -o "$output/ctype-$runtime.dll"
    dotnet "$output/ctype-$runtime.dll"
done
printf 'PASS: C-locale ctype table ABI, EOF, case conversion and classifications (system and portable)\n'
