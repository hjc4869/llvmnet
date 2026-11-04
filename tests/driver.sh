#!/usr/bin/env bash
# tags: toolchain aot cpp
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/driver"
mkdir -p "$output"
for runtime in system portable; do
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
grep -q 'requires baseline, x86-64-v2, x86-64-v3 or native' "$output/aot-isa.log"
if "$root/bin/llvmnet" --nativeaot --aot-instruction-set x86-64-v3 --runtime-id linux-arm64 "$root/tests/c/smoke.c" -o "$output/arm-v3" >"$output/aot-arm-v3.log" 2>&1; then
    printf 'FAIL: x86-64-v3 was accepted for an ARM64 target
' >&2
    exit 1
fi
grep -q 'x86-64-v3 is an x64 instruction set; the target is linux-arm64' "$output/aot-arm-v3.log"
# Native extensions (NX-03): a name that is not installed fails as a usage error, and a package whose header and -l name
# clash with the sysroot's cannot be used, by --extension or by -l, and --list-extensions shows why.
extension_root "$output/toolchain"
clash="$output/toolchain/sysroot/dotnet64-v2/ext/clash"
mkdir -p "$clash/include" "$clash/lib"
printf '{"name": "clash", "version": "0.1", "libraries": {"linux": ["libclash.so.1"]}, "link": ["m", "clash"], "headers": ["stdio.h"]}\n' >"$clash/manifest.json"
: >"$clash/include/stdio.h"
: >"$clash/lib/LlvmNet.Ext.Clash.dll"
result=0
LLVMNET_ROOT="$output/toolchain" "$root/bin/llvmnet" "$root/tests/c/smoke.c" --extension=missing -o "$output/missing.dll" 2>"$output/missing.log" || result=$?
test "$result" -eq 2
grep -qF "native extension 'missing' is not installed in $output/toolchain/sysroot/dotnet64-v2/ext; llvmnet --list-extensions lists the installed ones." "$output/missing.log"
clash_message="native extension 'clash' cannot be used: its header stdio.h clashes with the sysroot's stdio.h; -lm clashes with the sysroot's library of that name."
for option in --extension=clash -lclash; do
    if LLVMNET_ROOT="$output/toolchain" "$root/bin/llvmnet" "$root/tests/c/smoke.c" "$option" -o "$output/clash.dll" 2>"$output/clash.log"; then
        printf 'FAIL: %s linked an extension that clashes with the sysroot\n' "$option" >&2
        exit 1
    fi
    grep -qF "$clash_message" "$output/clash.log" || { cat "$output/clash.log" >&2; exit 1; }
done
LLVMNET_ROOT="$output/toolchain" "$root/bin/llvmnet" --list-extensions >"$output/extensions.txt"
grep -qF "  problem:   its header stdio.h clashes with the sysroot's stdio.h" "$output/extensions.txt"
(
    cd "$root"
    bin/llvmnet @tests/driver.rsp
)
result=0
"$output/response executable" || result=$?
test "$result" -eq 42
# Include options that take their directory or file as the next argument.
mkdir -p "$output/after"
printf '#define RESPONSE_VALUE 42\n' >"$output/after/response-value.h"
"$root/bin/llvmnet" -c -idirafter "$output/after" -include response-value.h "$root/tests/c/response.c" -o "$output/after.o"
"$root/bin/llvmnet" -M "$root/tests/c/core.c" -MF "$output/core.d"
grep -q core.c "$output/core.d"
"$root/bin/llvmnet" -c "$root/tests/c/link-main.c" -o"$output/main.o"
"$root/bin/llvmnet" -c "$root/tests/c/link-library.c" -o"$output/library.o"
"$root/bin/llvmnet" -r "$output/main.o" "$output/library.o" -o "$output/combined.o"
"$root/bin/llvmnet" "$output/combined.o" -o "$output/combined"
"$output/combined"
# -o may name a directory that does not exist yet, as for a JIT image.
rm -rf "$output/aot-new"
"$root/bin/llvmnet" --nativeaot "$root/tests/c/response.c" -DRESPONSE_VALUE=42 -o "$output/aot-new/aot-response"
result=0
LLVMNET_DOTNET=/does/not/exist "$output/aot-new/aot-response" || result=$?
test "$result" -eq 42
"$root/bin/llvmnet" --runtime=system --nativeaot "$root/tests/c/threads.c" -o "$output/system-aot-threads"
mkdir -p "$output/single-file"
cp "$output/system-aot-threads" "$output/single-file/threads"
"$output/single-file/threads"
"$root/bin/llvmnet" --runtime=system "$root/tests/cpp/core.cpp" -o "$output/system-cpp"
"$output/system-cpp"
printf 'PASS: response files, dependency output, joined -o, relocatable bitcode, real NativeAOT entry and native callbacks\n'