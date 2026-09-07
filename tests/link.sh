#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/link"
mkdir -p "$output"
compiler=(dotnet "$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll")
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
"${compiler[@]}" -c "$root/tests/c/link-main.c" -o "$output/main.o"
"${compiler[@]}" -c "$root/tests/c/link-library.c" -o "$output/library.o"
"${compiler[@]}" -c "$root/tests/c/link-unused.c" -o "$output/unused.o"
llvm-ar-22 rcs "$output/libexample.a" "$output/library.o" "$output/unused.o"
"${compiler[@]}" "$output/main.o" -L"$output" -lexample -o "$output/archive.dll"
dotnet "$output/archive.dll"
"${compiler[@]}" --emit-library "$output/library.o" -o "$output/example.dll"
"${compiler[@]}" "$output/main.o" --cil-reference "$output/example.dll" -o "$output/cil-link.dll"
dotnet "$output/cil-link.dll"
"${compiler[@]}" -fno-builtin "$root/tests/c/native-import.c" --native-import 'abs=libc.so.6!abs' -o "$output/native-import.dll"
dotnet "$output/native-import.dll"
if "${compiler[@]}" -fno-builtin "$root/tests/c/native-import.c" -o "$output/unresolved.dll" 2>"$output/unresolved.log"; then
    printf 'FAIL: unresolved native symbol linked without an explicit import\n' >&2
    exit 1
fi
grep -q 'Unresolved function: abs' "$output/unresolved.log"
if "${compiler[@]}" "$root/tests/c/native-aggregate.c" --native-import 'foreign_aggregate=libc.so.6!abs' -o "$output/invalid-abi.dll" 2>"$output/invalid-abi.log"; then
    printf 'FAIL: native aggregate ABI accepted by scalar-only P/Invoke linker\n' >&2
    exit 1
fi
grep -q 'requires a non-variadic scalar C ABI signature' "$output/invalid-abi.log"
printf 'PASS: bitcode archive, CIL reference, explicit P/Invoke and unresolved-symbol rejection\n'