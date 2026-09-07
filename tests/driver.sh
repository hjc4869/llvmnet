#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/driver"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
for runtime in managed-host system portable; do
    "$root/bin/llvmnet" -Wl,--version --runtime="$runtime" >"$output/linker-$runtime.txt"
    grep -q 'emits CIL, not native images' "$output/linker-$runtime.txt"
    "$root/bin/llvmnet" --runtime "$runtime" -Wl,--version >"$output/linker-$runtime.txt"
    grep -q 'emits CIL, not native images' "$output/linker-$runtime.txt"
done
if "$root/bin/llvmnet" --runtime=invalid -Wl,--version >"$output/linker-invalid.txt" 2>&1; then
    printf 'FAIL: linker query accepted an invalid runtime\n' >&2
    exit 1
fi
if "$root/bin/llvmnet" --runtime=portable -Wl,--version "$root/tests/c/response.c" >"$output/linker-input.txt" 2>&1; then
    printf 'FAIL: linker query silently discarded an input\n' >&2
    exit 1
fi
if "$root/bin/llvmnet" --aot-instruction-set native "$root/tests/c/response.c" -o "$output/invalid-aot-option" >"$output/aot-option.log" 2>&1; then
    printf 'FAIL: instruction-set selection without NativeAOT was accepted\n' >&2
    exit 1
fi
grep -q 'NativeAOT output settings require --nativeaot' "$output/aot-option.log"
if "$root/bin/llvmnet" --nativeaot --aot-instruction-set invalid "$root/tests/c/response.c" -o "$output/invalid-isa" >"$output/aot-isa.log" 2>&1; then
    printf 'FAIL: an invalid NativeAOT instruction set was accepted\n' >&2
    exit 1
fi
grep -q 'requires baseline or native' "$output/aot-isa.log"
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