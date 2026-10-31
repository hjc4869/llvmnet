#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/io"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/io.c" -o "$output/native"
"$output/native" "$output/native.bin" >"$output/native.txt"
for runtime in system portable; do
    for optimization in 0 1 2; do
        "$llvmnet" --runtime="$runtime" "-O$optimization" "$root/tests/c/io.c" -o "$output/io-O$optimization-$runtime.dll"
        dotnet "$output/io-O$optimization-$runtime.dll" "$output/cil-$runtime.bin" >"$output/cil-$runtime.txt"
        diff -u "$output/native.txt" "$output/cil-$runtime.txt"
    done
done
# Standard input redirected from a file seeks and stats as that file; from a pipe it does not seek.
printf 'abcdefghijklmnopqrstuvwxyz\nsecond line\nthird line\n' >"$output/stdin.txt"
clang-22 -O1 "$root/tests/c/stdin-seek.c" -o "$output/stdin-native"
"$llvmnet" "$root/tests/c/stdin-seek.c" -o "$output/stdin-seek.dll"
for program in "$output/stdin-native" "dotnet $output/stdin-seek.dll"; do
    # shellcheck disable=SC2086 # the program is a command with its arguments
    { $program <"$output/stdin.txt"; printf 'abcdefgh\n' | $program; } >"$output/stdin-${program##*/}.txt"
done
diff -u "$output/stdin-stdin-native.txt" "$output/stdin-stdin-seek.dll.txt"
printf 'PASS: stdio, file descriptors, stat layout, errno, parsing, scanf managed qsort callbacks (system and portable), and standard input redirected from a file seeks as that file\n'
