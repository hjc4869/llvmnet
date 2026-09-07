#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/modes"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
"$root/bin/llvmnet" --runtime=portable -shared "$root/tests/c/link-library.c" -o "$output/PortableLibrary.dll"
"$root/bin/llvmnet" --runtime=portable "$root/tests/c/link-main.c" --cil-reference "$output/PortableLibrary.dll" -o "$output/portable-client.dll"
dotnet "$output/portable-client.dll"
"$root/bin/llvmnet" -shared "$root/tests/c/link-library.c" -o "$output/LegacyLibrary.dll"
reject() {
    local diagnostic="$1"
    shift
    if "$root/bin/llvmnet" "$@" >"$output/rejected.out" 2>"$output/rejected.err"; then
        printf 'FAIL: invalid mode combination accepted\n' >&2
        exit 1
    fi
    grep -q "$diagnostic" "$output/rejected.err"
}
reject 'Managed reference ABI mismatch' --runtime=portable "$root/tests/c/link-main.c" --cil-reference "$output/LegacyLibrary.dll" -o "$output/bad.dll"
reject 'cannot contain native imports' --runtime=portable "$root/tests/c/smoke.c" --native-import 'abs=libc.so.6!abs' -o "$output/bad.dll"
reject 'requires --runtime=system' --runtime=portable "$root/tests/c/smoke.c" --system-library libc.so.6 -o "$output/bad.dll"
reject 'cannot change the versioned portable ABI' --runtime=portable -fshort-wchar "$root/tests/c/smoke.c" -o "$output/bad.dll"
reject 'cannot change the versioned portable ABI' --runtime=portable -funsigned-char "$root/tests/c/smoke.c" -o "$output/bad.dll"
reject 'requires an executable link' --nativeaot -c "$root/tests/c/smoke.c" -o "$output/bad.o"
reject 'require --nativeaot' --aot-debug "$root/tests/c/smoke.c" -o "$output/bad"
CPATH="$root/tests/host-headers" C_INCLUDE_PATH="$root/tests/host-headers" \
    "$root/bin/llvmnet" --runtime=portable "$root/tests/c/portable.c" -o "$output/isolated.dll"
dotnet "$output/isolated.dll"
"$root/bin/llvmnet" --runtime=system -fno-builtin "$root/tests/c/native-import.c" \
    --native-import 'abs=libc.so.6!abs' -o "$output/explicit-system-import"
"$output/explicit-system-import"
printf 'PASS: compatible portable CIL linking and explicit rejection of ABI/output mode conflicts\n'