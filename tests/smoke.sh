#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/smoke"
mkdir -p "$output"
clang-22 "$root/tests/c/smoke.c" -o "$output/native"
native_exit=0
"$output/native" || native_exit=$?
test "$native_exit" -eq 42
clang-22 "$root/tests/c/core.c" -o "$output/core-native"
"$output/core-native"
clang-22 "$root/tests/c/intrinsics.c" -lm -o "$output/intrinsics-native"
"$output/intrinsics-native"
clang-22 -O0 "$root/tests/c/byval.c" -o "$output/byval-native"
"$output/byval-native"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" "$root/tests/c/smoke.c" -o "$output/smoke-$runtime.dll"
    managed_exit=0
    dotnet "$output/smoke-$runtime.dll" || managed_exit=$?
    test "$managed_exit" -eq "$native_exit"
    printf 'PASS: %s: clang and CIL both returned %s\n' "$runtime" "$managed_exit"
    "$llvmnet" --runtime="$runtime" "$root/tests/c/core.c" -o "$output/core-$runtime.dll"
    dotnet "$output/core-$runtime.dll"
    printf 'PASS: %s: control flow, memory, aggregates, pointers, integers and floats\n' "$runtime"
    "$llvmnet" --runtime="$runtime" "$root/tests/c/intrinsics.c" -o "$output/intrinsics-$runtime.dll"
    dotnet "$output/intrinsics-$runtime.dll"
    printf 'PASS: %s: bit operations, checked arithmetic, rotations and scalar math\n' "$runtime"
    tag_ir "$runtime" "$root/tests/ir/switch.ll" "$output/switch-$runtime.bc"
    "$llvmnet" --runtime="$runtime" "$output/switch-$runtime.bc" -o "$output/switch-$runtime.dll"
    dotnet "$output/switch-$runtime.dll"
    printf 'PASS: %s: LLVM 22 switch cases, shared destinations and 64-bit case values\n' "$runtime"
    "$llvmnet" --runtime="$runtime" -O0 "$root/tests/c/byval.c" -o "$output/byval-$runtime.dll"
    dotnet "$output/byval-$runtime.dll"
    printf 'PASS: %s: large structs by value and hidden structure returns preserve caller memory\n' "$runtime"
done
