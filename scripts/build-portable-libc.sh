#!/usr/bin/env bash
# Build musl 1.2.5 for the dotnet64-v1 ABI as LLVM bitcode (plan item LC-01, spike stage).
#   scripts/build-portable-libc.sh [prefix]     default artifacts/portable-libc
# Produces <prefix>/include (musl headers for dotnet64-v1), <prefix>/lib/libc.a (bitcode archive) and
# <prefix>/build/failures.tsv (one row per source file llvmnet could not compile, with the first error).
# Upstream sources are not modified: arch/x86_64 is copied to arch/llvmnet and ports/musl is overlaid.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="$(realpath -m "${1:-$root/artifacts/portable-libc}")"
upstream="$("$root/scripts/fetch-sources.sh" --path musl)"
bash "$root/scripts/ensure-built.sh"
build="$prefix/build"
tree="$build/musl"
rm -rf "${build:?}" "${prefix:?}/include" "${prefix:?}/lib"
mkdir -p "$build" "$prefix/include" "$prefix/lib"
cp -a "$upstream" "$tree"
cp -a "$tree/arch/x86_64" "$tree/arch/llvmnet"
cp -a "$root/ports/musl/arch/llvmnet/." "$tree/arch/llvmnet/"
if [[ -d "$root/ports/musl/src" ]]; then cp -a "$root/ports/musl/src/." "$tree/src/"; fi
cd "$tree"
mkdir -p obj/include/bits obj/src/internal
sed -f tools/mkalltypes.sed arch/llvmnet/bits/alltypes.h.in include/alltypes.h.in >obj/include/bits/alltypes.h
cp arch/llvmnet/bits/syscall.h.in obj/include/bits/syscall.h
sed -n -e s/__NR_/SYS_/p <arch/llvmnet/bits/syscall.h.in >>obj/include/bits/syscall.h
printf '#define VERSION "1.2.5-llvmnet"\n' >obj/src/internal/version.h
# Source selection as in musl's Makefile with ARCH=llvmnet: generic C in src/*/ and src/malloc/mallocng/,
# where a src/<dir>/llvmnet/<name>.c from ports/musl replaces src/<dir>/<name>.c.
mapfile -t sources < <(
    { ls src/*/*.c src/malloc/mallocng/*.c; ls src/*/llvmnet/*.c 2>/dev/null || true; } | sort |
    awk '{ key = $0; sub("/llvmnet/", "/", key); chosen[key] = (index($0, "/llvmnet/") || !(key in chosen)) ? $0 : chosen[key] } END { for (k in chosen) print chosen[k] }' | sort)
printf '%s\n' "${sources[@]}" >"$build/sources.txt"
flags=(--runtime=portable -c -O2 -std=c99 -ffreestanding -nostdinc -D_XOPEN_SOURCE=700
    -I"$tree/arch/llvmnet" -I"$tree/arch/generic" -I"$tree/obj/src/internal" -I"$tree/src/include"
    -I"$tree/src/internal" -I"$tree/obj/include" -I"$tree/include"
    -fexcess-precision=standard -fno-strict-aliasing -Wno-everything)
# musl normally adds -frounding-math. The port uses the generic fenv (round-to-nearest only, no exception
# flags), so it is omitted; with it clang emits constrained floating-point intrinsics llvmnet does not lower yet.
printf '%s\n' "${flags[@]}" >"$build/flags.txt"
export LLVMNET_SKIP_BUILD=1 tree build root
compile() {
    local source="$1" object="$build/obj/${1%.c}.o"
    mkdir -p "$(dirname "$object")"
    mapfile -t flags <"$build/flags.txt"
    if ! "$root/bin/llvmnet" "${flags[@]}" "$tree/$source" -o "$object" >"$object.log" 2>&1; then
        printf '%s\t%s\n' "$source" "$(grep -m1 -E 'error' "$object.log" | sed "s|$tree/||" | tr '\t' ' ')" >>"$build/failures.tsv"
        rm -f "$object"
    fi
}
export -f compile
: >"$build/failures.tsv"
printf '%s\n' "${sources[@]}" | xargs -P "${JOBS:-$(nproc)}" -I{} bash -c 'compile "$1"' _ {}
sort -o "$build/failures.tsv" "$build/failures.tsv"
find "$build/obj" -name '*.o' | sort >"$build/objects.txt"
llvm-ar-22 rcs "$prefix/lib/libc.a" @"$build/objects.txt"
mapfile -t start_flags <"$build/flags.txt"
"$root/bin/llvmnet" "${start_flags[@]}" "$root/ports/musl/crt/llvmnet-start.c" -o "$prefix/lib/crt1.o"
cp -a include/. "$prefix/include/"
mkdir -p "$prefix/include/bits"
cp arch/generic/bits/*.h "$prefix/include/bits/"
cp arch/llvmnet/bits/*.h "$prefix/include/bits/"
cp obj/include/bits/alltypes.h obj/include/bits/syscall.h "$prefix/include/bits/"
cp -a "$root/ports/musl/include/." "$prefix/include/"
cp COPYRIGHT "$prefix/COPYRIGHT"
printf 'musl for dotnet64-v1: %d of %d sources compiled; failures in %s\n' \
    "$(wc -l <"$build/objects.txt")" "${#sources[@]}" "$build/failures.tsv"
