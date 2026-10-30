#!/usr/bin/env bash
# tags: toolchain aot
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/modes"
mkdir -p "$output"
"$root/bin/llvmnet" --runtime=portable -shared "$root/tests/c/link-library.c" -o "$output/PortableLibrary.dll"
"$root/bin/llvmnet" --runtime=portable "$root/tests/c/link-main.c" --cil-reference "$output/PortableLibrary.dll" -o "$output/portable-client.dll"
dotnet "$output/portable-client.dll"
"$root/bin/llvmnet" --runtime=system -shared "$root/tests/c/link-library.c" -o "$output/SystemLibrary.dll"
reject() {
    local diagnostic="$1"
    shift
    if "$root/bin/llvmnet" "$@" >"$output/rejected.out" 2>"$output/rejected.err"; then
        printf 'FAIL: invalid mode combination accepted\n' >&2
        exit 1
    fi
    grep -q "$diagnostic" "$output/rejected.err"
}
reject 'Managed reference ABI mismatch' --runtime=portable "$root/tests/c/link-main.c" --cil-reference "$output/SystemLibrary.dll" -o "$output/bad.dll"
reject 'cannot contain native imports' --runtime=portable "$root/tests/c/smoke.c" --native-import 'abs=libc.so.6!abs' -o "$output/bad.dll"
reject 'requires --runtime=system' --runtime=portable "$root/tests/c/smoke.c" --system-library libc.so.6 -o "$output/bad.dll"
reject 'cannot change the versioned portable ABI' --runtime=portable -fshort-wchar "$root/tests/c/smoke.c" -o "$output/bad.dll"
reject 'cannot change the versioned portable ABI' --runtime=portable -funsigned-char "$root/tests/c/smoke.c" -o "$output/bad.dll"
reject 'requires an executable link' --nativeaot -c "$root/tests/c/smoke.c" -o "$output/bad.o"
reject 'require --nativeaot' --aot-debug "$root/tests/c/smoke.c" -o "$output/bad"
reject 'managed-host was removed' --runtime=managed-host "$root/tests/c/smoke.c" -o "$output/bad.dll"
CPATH="$root/tests/host-headers" C_INCLUDE_PATH="$root/tests/host-headers" \
    "$root/bin/llvmnet" --runtime=portable "$root/tests/c/portable.c" -o "$output/isolated.dll"
dotnet "$output/isolated.dll"
"$root/bin/llvmnet" --runtime=system -fno-builtin "$root/tests/c/native-import.c" \
    --native-import 'abs=libc.so.6!abs' -o "$output/explicit-system-import"
"$output/explicit-system-import"
# System mode never links the portable sysroot: no musl definitions, and libc calls stay native imports.
"$root/bin/llvmnet" --runtime=system "$root/tests/c/runtime.c" --emit-llvm -o "$output/system-linked.bc"
if llvm-nm-22 --defined-only "$output/system-linked.bc" | grep -E ' (__init_libc|__stdio_write|__libc_start_main)$' >/dev/null; then
    printf 'FAIL: a system-mode link contains the portable C library\n' >&2
    exit 1
fi
llvm-nm-22 --undefined-only "$output/system-linked.bc" | grep -E ' (printf|puts)$' >/dev/null
# Nor its headers, in C or C++; portable mode sees only them and Clang's own resource headers.
for source_file in "$root/tests/c/runtime.c" "$root/tests/cpp/streams.cpp"; do
    "$root/bin/llvmnet" --runtime=system -H -fsyntax-only "$source_file" 2>"$output/system-headers.txt"
    if grep -q 'sysroot/dotnet64-v2' "$output/system-headers.txt"; then
        printf 'FAIL: system mode included a portable sysroot header for %s\n' "$source_file" >&2
        exit 1
    fi
    "$root/bin/llvmnet" --runtime=portable -H -fsyntax-only "$source_file" 2>"$output/portable-headers.txt"
    if grep '^\.' "$output/portable-headers.txt" | grep -v -e 'sysroot/dotnet64-v2' -e '/lib/clang/[0-9]*/include/' >/dev/null; then
        printf 'FAIL: portable mode included a header outside its sysroot for %s\n' "$source_file" >&2
        exit 1
    fi
done
printf 'PASS: compatible portable CIL linking and explicit rejection of ABI/output mode conflicts\n'