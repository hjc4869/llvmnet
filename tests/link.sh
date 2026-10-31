#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/link"
mkdir -p "$output"
for runtime in system portable; do
    compiler=("$llvmnet" --runtime="$runtime")
    "${compiler[@]}" -c "$root/tests/c/link-main.c" -o "$output/main-$runtime.o"
    "${compiler[@]}" -c "$root/tests/c/link-library.c" -o "$output/library-$runtime.o"
    "${compiler[@]}" -c "$root/tests/c/link-unused.c" -o "$output/unused-$runtime.o"
    llvm-ar-22 rcs "$output/libexample-$runtime.a" "$output/library-$runtime.o" "$output/unused-$runtime.o"
    "${compiler[@]}" "$output/main-$runtime.o" -L"$output" "-lexample-$runtime" -o "$output/archive-$runtime.dll"
    dotnet "$output/archive-$runtime.dll"
    "${compiler[@]}" --emit-library "$output/library-$runtime.o" -o "$output/Example${runtime^}.dll"
    "${compiler[@]}" "$output/main-$runtime.o" --cil-reference "$output/Example${runtime^}.dll" -o "$output/cil-link-$runtime.dll"
    dotnet "$output/cil-link-$runtime.dll"
done
if "$llvmnet" --runtime=portable "$output/main-system.o" -o "$output/mixed.dll" 2>"$output/mixed.log"; then
    printf 'FAIL: a system-mode object linked into a portable program\n' >&2
    exit 1
fi
grep -q 'ABI mismatch' "$output/mixed.log"
if "$llvmnet" --runtime=managed-host "$root/tests/c/smoke.c" -o "$output/legacy.dll" 2>"$output/legacy.log"; then
    printf 'FAIL: the removed managed-host mode was accepted\n' >&2
    exit 1
fi
grep -q 'managed-host was removed' "$output/legacy.log"
printf 'PASS: bitcode archives, CIL references and ABI mismatch rejection in both modes\n'
