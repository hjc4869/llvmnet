#!/usr/bin/env bash
# Build LLVM's OpenMP runtime (libomp) 22.1.8 for dotnet64-v2 on top of the musl and libc++ ports; a step of
# scripts/build-sysroot.sh.
#   scripts/build-portable-openmp.sh [prefix]    default artifacts/sysroot-build/dotnet64-v2 (must contain musl and libc++)
# Installs <prefix>/lib/libomp.a and <prefix>/include/omp.h; per-file failures go to <prefix>/build-openmp/failures.tsv.
# The build works on a copy of upstream with ports/openmp/llvmnet.patch applied (see ports/openmp/README.md) and
# generates what CMake would: kmp_config.h, omp.h and the message catalog.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="$(realpath -m "${1:-$root/artifacts/sysroot-build/dotnet64-v2}")"
[[ -f "$prefix/lib/libc.a" && -f "$prefix/lib/libc++.a" ]] || { printf 'Build musl and libc++ first for %s\n' "$prefix" >&2; exit 1; }
llvm="$("$root/scripts/fetch-sources.sh" --path llvm-project)"
bash "$root/scripts/ensure-built.sh"
build="$prefix/build-openmp"
tree="$build/runtime"
rm -rf "${build:?}"
mkdir -p "$build/generated" "$build/obj"
cp -a "$llvm/openmp/runtime" "$tree"
(cd "$tree" && patch -p1 --silent <"$root/ports/openmp/llvmnet.patch")
cp "$root/ports/openmp/llvmnet-support.cpp" "$tree/src/"
# kmp_config.h: a static runtime without OMPT, OMPD, ITT, statistics, hwloc, transactional locks or version symbols;
# aligned allocation from musl.
sed -E \
    -e 's|^#cmakedefine01 (LIBOMP_HAVE_WEAK_ATTRIBUTE\|LIBOMP_HAVE_ALIGNED_ALLOC\|LIBOMP_HAVE_POSIX_MEMALIGN)$|#define \1 1|' \
    -e 's|^#cmakedefine01 (.*)$|#define \1 0|' \
    -e 's|@LIBOMP_LEGAL_ARCH@|llvmnet|' -e 's|@LIBOMP_LIB_FILE@|libomp.a|' \
    -e 's|@LIBOMP_VERSION_MAJOR@|5|' -e 's|@LIBOMP_VERSION_MINOR@|0|' \
    "$tree/src/kmp_config.h.cmake" >"$build/generated/kmp_config.h"
sed -e 's|@LIBOMP_VERSION_MAJOR@|5|' -e 's|@LIBOMP_VERSION_MINOR@|0|' -e 's|@LIBOMP_VERSION_BUILD@|20140926|' \
    -e 's|@LIBOMP_BUILD_DATE@|No_Timestamp|' "$tree/src/include/omp.h.var" >"$build/generated/omp.h"
(cd "$build/generated" && python3 "$tree/tools/message-converter.py" --enum=kmp_i18n_id.inc "$tree/src/i18n/en_US.txt" &&
    python3 "$tree/tools/message-converter.py" --default=kmp_i18n_default.inc "$tree/src/i18n/en_US.txt")
# The sources CMake selects for a Linux build without the optional components, plus the port's own file.
sources=(kmp_alloc kmp_atomic kmp_csupport kmp_debug kmp_itt kmp_environment kmp_error kmp_global kmp_i18n kmp_io
    kmp_runtime kmp_settings kmp_str kmp_tasking kmp_threadprivate kmp_utility kmp_barrier kmp_wait_release
    kmp_affinity kmp_dispatch kmp_lock kmp_sched kmp_collapse z_Linux_util kmp_gsupport kmp_taskdeps kmp_cancel
    kmp_ftn_cdecl kmp_ftn_extra kmp_version llvmnet-support)
flags=(--runtime=portable --portable-sysroot "$prefix" -c -O2 -fno-vectorize -fno-slp-vectorize -std=c++17 -fno-exceptions -fno-rtti
    -I"$build/generated" -I"$tree/src" -I"$tree/src/i18n" -I"$tree/src/include" -I"$tree/src/thirdparty/ittnotify"
    -D_GNU_SOURCE -DKMP_USE_ASSERT=0 -Wno-everything)
printf '%s\n' "${flags[@]}" >"$build/flags.txt"
export LLVMNET_SKIP_BUILD=1 build tree root
compile() {
    local name="$1" object="$build/obj/$1.o"
    mapfile -t flags <"$build/flags.txt"
    if ! "$root/bin/llvmnet" "${flags[@]}" "$tree/src/$name.cpp" -o "$object" >"$object.log" 2>&1; then
        printf '%s\t%s\n' "$name" "$(grep -m1 -E 'error' "$object.log" | sed "s|$tree/||" | tr '\t' ' ')" >>"$build/failures.tsv"
        rm -f "$object"
    fi
}
export -f compile
: >"$build/failures.tsv"
printf '%s\n' "${sources[@]}" | xargs -P "${JOBS:-$(nproc)}" -I{} bash -c 'compile "$1"' _ {}
sort -o "$build/failures.tsv" "$build/failures.tsv"
mapfile -t objects < <(find "$build/obj" -name '*.o' | sort)
rm -f "$prefix/lib/libomp.a"
llvm-ar-22 rcs "$prefix/lib/libomp.a" "${objects[@]}"
cp "$build/generated/omp.h" "$prefix/include/omp.h"
printf 'libomp for dotnet64-v2: %d of %d sources compiled; failures in %s\n' "${#objects[@]}" "${#sources[@]}" "$build/failures.tsv"
