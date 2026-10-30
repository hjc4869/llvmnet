#!/usr/bin/env bash
# tags: toolchain cpp
# requires: clang++
# Causes of portable SPEC failures recorded in docs/spec-portable-limitations.md: resolved causes must build and
# exit like their native builds, and the remaining ones must still fail with their diagnostic.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/portable-spec-causes"
rm -rf "$output"
mkdir -p "$output"
cd "$output"
resolved() {
    local name="$1" native_compiler="$2" source_file="$3"
    shift 3
    local native=0 managed=0
    "$native_compiler" -w -O0 -fno-builtin "$@" "$source_file" -lm -pthread -o "$name-native"
    "./$name-native" >/dev/null || native=$?
    "$llvmnet" --runtime=portable -O0 -fno-builtin "$@" "$source_file" -o "$name.dll" >"$name.log" 2>&1 ||
        { printf 'FAIL: %s no longer builds; see %s\n' "$name" "$output/$name.log" >&2; exit 1; }
    dotnet "$name.dll" >/dev/null || managed=$?
    if [[ "$native" != "$managed" ]]; then
        printf 'FAIL: %s exits with %s, native with %s\n' "$name" "$managed" "$native" >&2
        exit 1
    fi
    printf 'RESOLVED: %s\n' "$name"
}
expect_failure() {
    local name="$1" diagnostic="$2"
    shift 2
    if "$@" >"$name.log" 2>&1; then
        printf 'Unexpected success: %s; update the limitation document.\n' "$name" >&2
        exit 1
    fi
    if ! grep -Fq -- "$diagnostic" "$name.log"; then
        printf 'Wrong diagnostic for %s; see %s\n' "$name" "$output/$name.log" >&2
        exit 1
    fi
    printf 'STILL REJECTED: %s (%s)\n' "$name" "$diagnostic"
}
for header in sys/ioctl.h sys/param.h complex.h locale.h malloc.h sys/resource.h; do
    resolved "header-${header//\//-}" clang-22 "$root/tests/c/portable-spec-header.c" "-DPROBE_HEADER=<$header>"
done
# Not part of a C library: Linux kernel headers, BSD machine headers and OpenMP.
for header in linux/limits.h machine/endian.h omp.h; do
    expect_failure "header-${header//\//-}" "'$header' file not found" \
        "$llvmnet" --runtime=portable -c "-DPROBE_HEADER=<$header>" "$root/tests/c/portable-spec-header.c" -o header.o
done
# system, dup2, freopen, hypotl, cexp, and SPEC's pthread stubs, which need sys/types.h to declare the pthread types as glibc does.
for probe in 1 2 3 4 5; do
    resolved "c-$probe" clang-22 "$root/tests/c/portable-spec-missing.c" "-DPROBE=$probe"
done
resolved c-7 clang-22 "$root/tests/c/portable-spec-missing.c" -DPROBE=7 -DSPEC
# A call through a prototype that contradicts the C library's declaration is rejected, not miscompiled.
expect_failure c-9 'Incompatible direct call signature for open' \
    "$llvmnet" --runtime=portable -O0 -fno-builtin -DPROBE=9 "$root/tests/c/portable-spec-missing.c" -o probe.dll
expect_failure openmp 'Unresolved function: __kmpc_fork_call' \
    "$llvmnet" --runtime=portable -O0 -fopenmp -DPROBE=6 "$root/tests/c/portable-spec-missing.c" -o openmp.dll
# iostream, sstream, fstream, complex and std::thread.
for probe in 1 2 3 4 5; do
    resolved "cpp-$probe" clang++-22 "$root/tests/cpp/portable-spec-missing.cpp" -std=c++17 "-DPROBE=$probe"
done
expect_failure unsigned-char 'cannot change the versioned portable ABI' \
    "$llvmnet" --runtime=portable -funsigned-char "$root/tests/c/portable-spec-missing.c" -o char.dll
expect_failure native-openmp '--system-library requires --runtime=system' \
    "$llvmnet" --runtime=portable --system-library libomp.so.5 "$root/tests/c/portable-spec-missing.c" -o native.dll
printf 'PASS: 17 recorded portable SPEC causes are resolved and match native; 7 remaining causes are still rejected\n'
