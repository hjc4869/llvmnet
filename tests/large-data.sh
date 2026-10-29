#!/usr/bin/env bash
# tags: core aot
# Initialized globals larger than the CLR data-field limit, and globals beyond 2 GB.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/large-data"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/large-data.c" -o "$output/native"
"$output/native" > "$output/native.txt"
for runtime in system portable; do
    target="$output/data-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -O1 "$root/tests/c/large-data.c" -o "$target.dll"
    dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O1 "$root/tests/c/large-data.c" -o "$target-aot"
    "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: %s globals larger than the CLR data-field limit in JIT/NativeAOT\n' "$runtime"
    # Beyond 2 GB there is no native oracle: the initialized record would make a 2 GB executable.
    "$root/bin/llvmnet" --runtime="$runtime" -O0 "$root/tests/c/huge-globals.c" -o "$output/huge-$runtime.dll"
    dotnet "$output/huge-$runtime.dll" > "$output/huge-$runtime.txt"
    diff -u <(printf 'globals beyond 2 GB: 3221225472 and 2147496012 bytes passed\n') "$output/huge-$runtime.txt"
    printf 'PASS: %s globals beyond 2 GB\n' "$runtime"
done