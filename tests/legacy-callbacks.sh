#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/legacy-callbacks"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
for optimization in 0 3; do
    clang-22 -std=gnu17 -Wno-deprecated-non-prototype -O"$optimization" "$root/tests/c/legacy-callbacks.c" -o "$output/native"
    "$output/native" > "$output/native.txt"
    for runtime in system portable; do
        target="$output/callbacks-$runtime-O$optimization"
        "$root/bin/llvmnet" --runtime="$runtime" -std=gnu17 -Wno-deprecated-non-prototype -O"$optimization" "$root/tests/c/legacy-callbacks.c" -o "$target.dll"
        dotnet "$target.dll" > "$target.txt"
        diff -u "$output/native.txt" "$target.txt"
        "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -std=gnu17 -Wno-deprecated-non-prototype -O"$optimization" "$root/tests/c/legacy-callbacks.c" -o "$target-aot"
        "$target-aot" > "$target-aot.txt"
        diff -u "$output/native.txt" "$target-aot.txt"
        printf 'PASS: %s O%s legacy fixed callbacks in JIT/NativeAOT\n' "$runtime" "$optimization"
    done
done
clang-22 -O0 "$root/tests/ir/system/legacy-register-width.ll" -o "$output/registers-native"
"$output/registers-native"
cp "$root/tests/ir/system/legacy-register-width.ll" "$output/registers.input"
"$root/bin/llvmnet" --runtime=system -x ir -O0 -c "$output/registers.input" -o "$output/registers.bc"
"$root/bin/llvmnet" --runtime=system "$output/registers.bc" -o "$output/registers.dll"
dotnet "$output/registers.dll"
"$root/bin/llvmnet" --runtime=system --nativeaot "$output/registers.bc" -o "$output/registers-aot"
"$output/registers-aot"
"$root/bin/llvmnet" --runtime=portable -x ir -O0 -c "$output/registers.input" -o "$output/registers-portable.bc"
if "$root/bin/llvmnet" --runtime=portable "$output/registers-portable.bc" -o "$output/rejected.dll" > "$output/registers-portable.log" 2>&1; then
    printf 'FAIL: portable mode accepted a system register-width mismatch\n' >&2
    exit 1
fi
grep -q 'Incompatible direct call signature' "$output/registers-portable.log"
cp "$root/tests/ir/rejected/legacy-stack-width.ll" "$output/stack.input"
"$root/bin/llvmnet" --runtime=system -x ir -O0 -c "$output/stack.input" -o "$output/stack.bc"
if "$root/bin/llvmnet" --runtime=system "$output/stack.bc" -o "$output/rejected.dll" > "$output/stack.log" 2>&1; then
    printf 'FAIL: system mode accepted a stack-passed width mismatch\n' >&2
    exit 1
fi
grep -q 'Incompatible direct call signature' "$output/stack.log"
printf 'PASS: system integer register compatibility and stack narrowing in JIT/NativeAOT, with portable and stack widening rejected\n'
ulimit -c 0
cp "$root/tests/ir/system/missing-arguments.ll" "$output/missing.input"
"$root/bin/llvmnet" --runtime=system -x ir -O0 -c "$output/missing.input" -o "$output/missing.bc"
if "$root/bin/llvmnet" --runtime=system "$output/missing.bc" -o "$output/missing-strict.dll" > "$output/missing-strict.log" 2>&1; then
    printf 'FAIL: missing arguments were accepted without an explicit trap option\n' >&2
    exit 1
fi
grep -q 'Incompatible direct call signature' "$output/missing-strict.log"
"$root/bin/llvmnet" --runtime=system --trap-missing-arguments "$output/missing.bc" -o "$output/missing.dll" 2> "$output/missing-build.log"
grep -q 'emitting runtime trap' "$output/missing-build.log"
dotnet "$output/missing.dll"
if dotnet "$output/missing.dll" invoke > "$output/missing-jit.log" 2>&1; then
    printf 'FAIL: invalid call did not trap under JIT\n' >&2
    exit 1
fi
grep -q 'Invalid direct call with missing arguments' "$output/missing-jit.log"
"$root/bin/llvmnet" --runtime=system --trap-missing-arguments --nativeaot "$output/missing.bc" -o "$output/missing-aot"
"$output/missing-aot"
if "$output/missing-aot" invoke > "$output/missing-aot.log" 2>&1; then
    printf 'FAIL: invalid call did not trap under NativeAOT\n' >&2
    exit 1
fi
grep -q 'Invalid direct call with missing arguments' "$output/missing-aot.log"
if "$root/bin/llvmnet" --runtime=portable --trap-missing-arguments "$output/registers-portable.bc" -o "$output/missing-portable.dll" > "$output/missing-portable.log" 2>&1; then
    printf 'FAIL: portable mode accepted the invalid-call trap option\n' >&2
    exit 1
fi
grep -q -- '--trap-missing-arguments requires --runtime=system' "$output/missing-portable.log"
printf 'PASS: opt-in missing-argument traps fail on execution in JIT/NativeAOT; strict defaults retained\n'