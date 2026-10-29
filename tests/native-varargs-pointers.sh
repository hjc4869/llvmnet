#!/usr/bin/env bash
# tags: core aot
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/native-varargs-pointers"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/native-varargs-pointers.c" -o "$output/native"
"$output/native" "$output/native-file" > "$output/native.txt"
"$root/bin/llvmnet" --runtime=system -O1 "$root/tests/c/native-varargs-pointers.c" -o "$output/pointers.dll"
dotnet "$output/pointers.dll" "$output/jit-file" > "$output/jit.txt"
diff -u "$output/native.txt" "$output/jit.txt"
"$root/bin/llvmnet" --runtime=system --nativeaot -O1 "$root/tests/c/native-varargs-pointers.c" -o "$output/pointers-aot"
"$output/pointers-aot" "$output/aot-file" > "$output/aot.txt"
diff -u "$output/native.txt" "$output/aot.txt"
printf 'PASS: native variadic pointers, mixed register/stack arguments, open and fcntl in JIT/NativeAOT\n'