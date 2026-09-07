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
clang-22 -O3 "$root/tests/c/libgen.c" -o "$output/libgen-native"
"$output/libgen-native"
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -O3 "$root/tests/c/libgen.c" -o "$output/libgen-$runtime.dll"
    dotnet "$output/libgen-$runtime.dll"
done
printf 'PASS: POSIX basename/dirname including trailing slashes, roots, empty and null paths in both ABIs\n'
clang-22 -O3 "$root/tests/c/utime.c" -o "$output/utime-native"
(cd "$output" && ./utime-native)
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -O3 "$root/tests/c/utime.c" -o "$output/utime-$runtime.dll"
    (cd "$output" && dotnet "utime-$runtime.dll")
done
printf 'PASS: POSIX utime explicit/current timestamps and missing-file errno in both ABIs\n'
clang-22 -O3 "$root/tests/c/tokenize.c" -o "$output/tokenize-native"
"$output/tokenize-native"
for runtime in system portable; do
    "$root/bin/llvmnet" --runtime="$runtime" -O3 "$root/tests/c/tokenize.c" -o "$output/tokenize-$runtime.dll"
    dotnet "$output/tokenize-$runtime.dll"
done
printf 'PASS: strtok and strtok_r delimiter changes, empty input and independent parser state in both ABIs\n'