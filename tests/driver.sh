#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/driver"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
(
    cd "$root"
    bin/llvmnet @tests/driver.rsp
)
result=0
"$output/response executable" || result=$?
test "$result" -eq 42
"$root/bin/llvmnet" -M "$root/tests/c/core.c" -MF "$output/core.d"
grep -q core.c "$output/core.d"
"$root/bin/llvmnet" -c "$root/tests/c/link-main.c" -o"$output/main.o"
"$root/bin/llvmnet" -c "$root/tests/c/link-library.c" -o"$output/library.o"
"$root/bin/llvmnet" -r "$output/main.o" "$output/library.o" -o "$output/combined.o"
"$root/bin/llvmnet" "$output/combined.o" -o "$output/combined"
"$output/combined"
"$root/bin/llvmnet" --nativeaot "$root/tests/c/response.c" -DRESPONSE_VALUE=42 -o "$output/aot-response"
result=0
LLVMNET_DOTNET=/does/not/exist "$output/aot-response" || result=$?
test "$result" -eq 42
"$root/bin/llvmnet" --runtime=system --nativeaot "$root/tests/c/threads.c" -o "$output/system-aot-threads"
mkdir -p "$output/single-file"
cp "$output/system-aot-threads" "$output/single-file/threads"
"$output/single-file/threads"
"$root/bin/llvmnet" --runtime=system "$root/tests/cpp/core.cpp" -o "$output/system-cpp"
"$output/system-cpp"
printf 'PASS: response files, dependency output, joined -o, relocatable bitcode, real NativeAOT entry and native callbacks\n'