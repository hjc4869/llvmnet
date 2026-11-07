#!/usr/bin/env bash
# Build mimalloc 3.6.1 for dotnet64-v2 as the portable C library's allocator; a step of scripts/build-sysroot.sh, right
# after musl (scripts/build-portable-libc.sh), which leaves musl's own allocator out of libc.a.
#   scripts/build-portable-mimalloc.sh [prefix]    default artifacts/sysroot-build/dotnet64-v2 (must already contain musl)
# Adds mimalloc.o to <prefix>/lib/libc.a; a compile failure goes to <prefix>/build-mimalloc/failures.tsv. The build works
# on a copy of upstream with ports/mimalloc/llvmnet.patch applied and the port's files overlaid (see
# ports/mimalloc/README.md): all of mimalloc is one translation unit, ports/mimalloc/llvmnet-libc.c, which also defines
# musl's names for the allocation functions.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="$(realpath -m "${1:-$root/artifacts/sysroot-build/dotnet64-v2}")"
[[ -f "$prefix/lib/libc.a" ]] || { printf 'Build musl first: scripts/build-portable-libc.sh %s\n' "$prefix" >&2; exit 1; }
upstream="$("$root/scripts/fetch-sources.sh" --path mimalloc)"
bash "$root/scripts/ensure-built.sh"
build="$prefix/build-mimalloc"
tree="$build/mimalloc"
rm -rf "${build:?}"
mkdir -p "$build"
cp -a "$upstream" "$tree"
(cd "$tree" && patch -p1 --silent <"$root/ports/mimalloc/llvmnet.patch")
cp -a "$root/ports/mimalloc/src/." "$tree/src/"
cp "$root/ports/mimalloc/llvmnet-libc.c" "$tree/src/"
# A release build (NDEBUG) as the C library's allocator: no profiler, which writes files with stdio, no fork handler,
# because fork is not available, and no weak std::get_new_handler, because libc++abi's operator new calls malloc.
# free finds a block's page through mimalloc's page map instead of 256 MiB-aligned page metadata: host memory cannot
# be trimmed to an alignment, so each arena would cost twice its size, and the page map measured faster as well.
# __x86_64 is undefined so that mimalloc assumes no processor: it would size its page map for x86-64's 47-bit
# addresses, but the image also runs on hosts whose .NET hands out 48-bit ones (Linux ARM64).
# The runtime libraries stay scalar, as SV-05 measured them (ADR-11).
flags=(--runtime=portable --portable-sysroot "$prefix" -c -O2 -fno-vectorize -fno-slp-vectorize -std=c11
    -I"$tree/include" -I"$tree/src" -U__x86_64 -DNDEBUG -DMI_LIBC_MUSL=1 -DMI_FREE_USE_PAGEMAP=1 -DMI_PROFILE=0
    -DMI_NO_PTHREAD_ATFORK=1 -DMI_NO_CXX_NEW_HANDLER=1 -fno-builtin-malloc -Wno-everything)
printf '%s\n' "${flags[@]}" >"$build/flags.txt"
: >"$build/failures.tsv"
export LLVMNET_SKIP_BUILD=1
if ! "$root/bin/llvmnet" "${flags[@]}" "$tree/src/llvmnet-libc.c" -o "$build/mimalloc-all.o" >"$build/mimalloc.log" 2>&1; then
    printf 'llvmnet-libc.c\t%s\n' "$(grep -m1 -E 'error' "$build/mimalloc.log" | sed "s|$tree/||" | tr '\t' ' ')" >>"$build/failures.tsv"
    printf 'mimalloc for dotnet64-v2 did not compile; failures in %s\n' "$build/failures.tsv"
    exit 0
fi
# Only the C library's names stay visible: a program may define names of mimalloc's own (or link a mimalloc of its
# own), and the parts of mimalloc's API that the C names do not reach drop out.
exports=malloc,calloc,realloc,free,aligned_alloc,malloc_usable_size,__libc_malloc,__libc_calloc,__libc_realloc,__libc_free,__malloc_donate
opt-22 -passes=internalize,globaldce -internalize-public-api-list="$exports" "$build/mimalloc-all.o" -o "$build/mimalloc.o"
llvm-ar-22 rcs "$prefix/lib/libc.a" "$build/mimalloc.o"
cp "$tree/LICENSE" "$prefix/mimalloc.LICENSE"
printf 'mimalloc %s for dotnet64-v2: added to %s\n' "$(awk -F '\t' '$1 == "mimalloc" { print $2 }' "$root/scripts/sources.lock")" "$prefix/lib/libc.a"
