#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/smoke"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler/LlvmNet.Compiler.csproj" -c Release --nologo
clang-22 "$root/tests/c/smoke.c" -o "$output/native"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$root/tests/c/smoke.c" -o "$output/managed.dll"
native_exit=0
managed_exit=0
"$output/native" || native_exit=$?
dotnet "$output/managed.dll" || managed_exit=$?
test "$native_exit" -eq 42
test "$managed_exit" -eq "$native_exit"
printf 'PASS: clang and CIL both returned %s\n' "$managed_exit"
clang-22 "$root/tests/c/core.c" -o "$output/core-native"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$root/tests/c/core.c" -o "$output/core.dll"
"$output/core-native"
dotnet "$output/core.dll"
printf 'PASS: control flow, memory, aggregates, pointers, integers and floats\n'
clang-22 "$root/tests/c/intrinsics.c" -lm -o "$output/intrinsics-native"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$root/tests/c/intrinsics.c" -o "$output/intrinsics.dll"
"$output/intrinsics-native"
dotnet "$output/intrinsics.dll"
printf 'PASS: bit operations, checked arithmetic, rotations and scalar math\n'
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" "$root/tests/ir/switch.ll" -o "$output/switch.dll"
dotnet "$output/switch.dll"
printf 'PASS: LLVM 22 switch cases, shared destinations and 64-bit case values\n'
clang-22 -O0 "$root/tests/c/byval.c" -o "$output/byval-native"
"$output/byval-native"
dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll" -O0 "$root/tests/c/byval.c" -o "$output/byval.dll"
dotnet "$output/byval.dll"
printf 'PASS: large structs by value and hidden structure returns preserve caller memory\n'