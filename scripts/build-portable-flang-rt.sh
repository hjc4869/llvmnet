#!/usr/bin/env bash
# Build the upstream Fortran runtime (flang-rt 22.1.8) for dotnet64-v2 on musl and libc++ (plan items FT-01, FT-05);
# the last step of scripts/build-sysroot.sh.
#   scripts/build-portable-flang-rt.sh [prefix]   default artifacts/sysroot-build/dotnet64-v2 (must contain musl and libc++)
# Installs <prefix>/lib/libflang_rt.runtime.a; per-file failures go to <prefix>/build-flang-rt/failures.tsv.
# REAL(16) is not available: flang disables __float128 with libc++, and dotnet64-v2 has no fp128 yet (BE-14).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="$(realpath -m "${1:-$root/artifacts/sysroot-build/dotnet64-v2}")"
[[ -f "$prefix/lib/libc++.a" ]] || { printf 'Build musl and libc++ first (build-portable-libc.sh, build-portable-cxx.sh) into %s\n' "$prefix" >&2; exit 1; }
llvm="$("$root/scripts/fetch-sources.sh" --path llvm-project)"
bash "$root/scripts/ensure-built.sh"
build="$prefix/build-flang-rt"
rm -rf "${build:?}"
mkdir -p "$build/generated"
sed -e 's/#cmakedefine01 HAVE_STRERROR_R/#define HAVE_STRERROR_R 1/' -e 's/#cmakedefine01 HAVE_DECL_STRERROR_S/#define HAVE_DECL_STRERROR_S 0/' \
    -e '/#cmakedefine HAVE_BACKTRACE/d' -e 's/\${BACKTRACE_HEADER}/execinfo.h/' \
    "$llvm/flang-rt/cmake/config.h.cmake.in" >"$build/generated/config.h"
runtime="$llvm/flang-rt/lib/runtime"
sources=("$llvm/flang/lib/Decimal/binary-to-decimal.cpp" "$llvm/flang/lib/Decimal/decimal-to-binary.cpp")
for name in ISO_Fortran_binding allocator-registry allocatable array-constructor assign buffer character connection copy \
    derived-api derived descriptor-io descriptor dot-product edit-input edit-output environment exceptions external-unit \
    extrema file findloc format inquiry internal-unit io-api io-api-minimal io-error io-stmt iostat matmul-transpose matmul \
    memory misc-intrinsic namelist non-tbp-dio numeric pointer product pseudo-unit ragged reduction stat stop sum support \
    terminator tools transformational type-code type-info unit utf work-queue command complex-powi execute extensions main \
    random reduce temporary-stack time-intrinsic unit-map; do
    sources+=("$runtime/$name.cpp")
done
sources+=("$runtime/complex-reduction.c")
printf '%s\n' --runtime=portable --portable-sysroot "$prefix" -c -O2 -D__linux__ -D__unix__ -Wno-everything \
    -I"$llvm/flang-rt/include" -I"$llvm/flang/include" -I"$build/generated" -I"$runtime" -D_GLIBCXX_NO_ASSERTIONS >"$build/flags.txt"
export LLVMNET_SKIP_BUILD=1 build root llvm
compile() {
    local source="$1" object
    object="$build/obj/$(basename "${source%.*}").o"
    mkdir -p "$build/obj"
    mapfile -t flags <"$build/flags.txt"
    local standard=(-std=c++17)
    [[ "$source" == *.c ]] && standard=(-std=c11)
    if ! "$root/bin/llvmnet" "${flags[@]}" "${standard[@]}" "$source" -o "$object" >"$object.log" 2>&1; then
        printf '%s\t%s\n' "${source#"$llvm"/}" "$(grep -m1 -E 'error' "$object.log" | sed "s|$llvm/||" | tr '\t' ' ')" >>"$build/failures.tsv"
        rm -f "$object"
    fi
}
export -f compile
: >"$build/failures.tsv"
printf '%s\n' "${sources[@]}" | xargs -P "${JOBS:-$(nproc)}" -I{} bash -c 'compile "$1"' _ {}
sort -o "$build/failures.tsv" "$build/failures.tsv"
rm -f "$prefix/lib/libflang_rt.runtime.a"
mapfile -t objects < <(find "$build/obj" -name '*.o' | sort)
llvm-ar-22 rcs "$prefix/lib/libflang_rt.runtime.a" "${objects[@]}"
printf 'flang-rt for dotnet64-v2: %d of %d sources compiled; failures in %s\n' "${#objects[@]}" "${#sources[@]}" "$build/failures.tsv"
