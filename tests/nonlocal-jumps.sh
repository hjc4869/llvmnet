#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/nonlocal-jumps"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
for optimization in 0 3; do
    clang-22 -O"$optimization" "$root/tests/c/nonlocal-jumps.c" -o "$output/native"
    "$output/native" > "$output/native.txt"
    for runtime in system portable; do
        target="$output/jumps-$runtime-O$optimization"
        "$root/bin/llvmnet" --runtime="$runtime" -O"$optimization" "$root/tests/c/nonlocal-jumps.c" -o "$target.dll"
        dotnet "$target.dll" > "$target.txt"
        diff -u "$output/native.txt" "$target.txt"
        "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -O"$optimization" "$root/tests/c/nonlocal-jumps.c" -o "$target-aot"
        "$target-aot" > "$target-aot.txt"
        diff -u "$output/native.txt" "$target-aot.txt"
        printf 'PASS: %s O%s nonlocal jumps in JIT and NativeAOT\n' "$runtime" "$optimization"
    done
done
clang-22 -O3 "$root/tests/c/nonlocal-jumps-signals.c" -o "$output/signals-native"
"$output/signals-native" > "$output/signals-native.txt"
"$root/bin/llvmnet" --runtime=system -O3 "$root/tests/c/nonlocal-jumps-signals.c" -o "$output/signals.dll"
dotnet "$output/signals.dll" > "$output/signals.txt"
diff -u "$output/signals-native.txt" "$output/signals.txt"
"$root/bin/llvmnet" --runtime=system --nativeaot -O3 "$root/tests/c/nonlocal-jumps-signals.c" -o "$output/signals-aot"
"$output/signals-aot" > "$output/signals-aot.txt"
diff -u "$output/signals-native.txt" "$output/signals-aot.txt"
printf 'PASS: sigsetjmp restores saved masks and leaves unsaved masks unchanged\n'