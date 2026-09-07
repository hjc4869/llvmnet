#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/portable"
mkdir -p "$output"
dotnet build "$root/llvmnet.slnx" -c Release --nologo
for fixture in portable core runtime threads time; do
    "$root/bin/llvmnet" --runtime=portable "$root/tests/c/$fixture.c" -o "$output/$fixture.dll"
    if [[ "$fixture" == runtime ]]; then dotnet "$output/$fixture.dll" payload; else dotnet "$output/$fixture.dll"; fi
    dotnet "$root/tests/LlvmNet.Checks/bin/Release/net10.0/LlvmNet.Checks.dll" inspect "$output/$fixture.dll"
done
"$root/bin/llvmnet" --runtime=portable "$root/tests/c/portable-fortran.c" "$root/tests/fortran/portable-interop.f90" -o "$output/interop.dll"
dotnet "$output/interop.dll"
"$root/bin/llvmnet" --runtime=portable --nativeaot "$root/tests/c/portable.c" -o "$output/portable-aot"
"$output/portable-aot"
if "$root/bin/llvmnet" --runtime=portable -include /usr/include/stdio.h "$root/tests/c/smoke.c" -o "$output/host-header" 2>"$output/header.log"; then
    printf 'FAIL: portable compilation used incompatible host headers\n' >&2
    exit 1
fi
printf 'PASS: versioned portable ABI, isolated headers, managed CIL output and portable-mode NativeAOT\n'