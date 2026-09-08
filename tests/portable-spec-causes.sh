#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$root/artifacts/tests/portable-spec/causes"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
expect_failure() {
    local name="$1" diagnostic="$2"
    shift 2
    if "$@" > "$output/$name.log" 2>&1; then
        printf 'Unexpected success: %s; update the limitation document.\n' "$name" >&2
        exit 1
    fi
    if ! grep -Fq -- "$diagnostic" "$output/$name.log"; then
        printf 'Wrong diagnostic for %s; see %s\n' "$name" "$output/$name.log" >&2
        exit 1
    fi
    printf 'REPRODUCED: %s (%s)\n' "$name" "$diagnostic"
}
for header in sys/ioctl.h sys/param.h complex.h locale.h malloc.h linux/limits.h sys/resource.h machine/endian.h omp.h; do
    expect_failure "header-${header//\//-}" "'$header' file not found" \
        "$root/bin/llvmnet" --runtime=portable -c "-DPROBE_HEADER=<$header>" \
        "$root/tests/c/portable-spec-header.c" -o "$output/header.o"
done
for specification in '1:system' '2:dup2' '3:freopen' '4:hypotl' '5:Unresolved function: cexp' '9:Runtime ABI mismatch for open'; do
    probe="${specification%%:*}"
    expect_failure "c-$probe" "${specification#*:}" \
        "$root/bin/llvmnet" --runtime=portable -O0 -fno-builtin "-DPROBE=$probe" \
        "$root/tests/c/portable-spec-missing.c" -o "$output/probe.dll"
done
expect_failure openmp 'Unresolved function: __kmpc_fork_call' \
    "$root/bin/llvmnet" --runtime=portable -O0 -fopenmp -DPROBE=6 \
    "$root/tests/c/portable-spec-missing.c" -o "$output/openmp.dll"
expect_failure pthread-stub 'unknown type name' \
    "$root/bin/llvmnet" --runtime=portable -DPROBE=7 -DSPEC \
    "$root/tests/c/portable-spec-missing.c" -o "$output/stub.dll"
"$root/bin/llvmnet" --runtime=portable -DPROBE=7 -DSPEC -DSPEC_PTHREAD_STUBS_NEED_PTHREAD_TYPES \
    "$root/tests/c/portable-spec-missing.c" -o "$output/stub-fixed.dll"
dotnet "$output/stub-fixed.dll"
for specification in '1:invalid operands' '2:undefined template' '3:undefined template' '4:unresolved using declaration' '5:thread4joinEv'; do
    probe="${specification%%:*}"
    expect_failure "cpp-$probe" "${specification#*:}" \
        "$root/bin/llvmnet" --runtime=portable -std=c++17 -O0 "-DPROBE=$probe" \
        "$root/tests/cpp/portable-spec-missing.cpp" -o "$output/probe.dll"
done
expect_failure unsigned-char 'cannot change the versioned portable ABI' \
    "$root/bin/llvmnet" --runtime=portable -funsigned-char "$root/tests/c/portable-spec-missing.c" -o "$output/char.dll"
expect_failure native-openmp '--system-library requires --runtime=system' \
    "$root/bin/llvmnet" --runtime=portable --system-library libomp.so.5 "$root/tests/c/portable-spec-missing.c" -o "$output/native.dll"
printf 'PASS: documented portable SPEC causes reproduced; these expected rejections are not benchmark passes\n'