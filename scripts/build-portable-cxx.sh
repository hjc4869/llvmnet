#!/usr/bin/env bash
# Build upstream libc++ and libc++abi 22.1.8 for dotnet64-v2 on top of the musl port; a step of
# scripts/build-sysroot.sh.
#   scripts/build-portable-cxx.sh [prefix]    default artifacts/sysroot-build/dotnet64-v2 (must already contain musl)
# Installs <prefix>/include/c++/v1 and <prefix>/lib/{libc++.a,libc++abi.a}; per-file failures go to
# <prefix>/build-cxx/failures.tsv. The CLR unwinds the stack, so throwing, catching and terminate handlers live in the
# managed bridge (src/LlvmNet.Runtime/Cxx.cs) and libc++abi's cxa_exception, cxa_personality and handler sources are
# left out; type_info, dynamic_cast and catch matching come from private_typeinfo and
# ports/libcxxabi/llvmnet-catch.cpp (docs/dev/cxx-eh-bridge.md).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="$(realpath -m "${1:-$root/artifacts/sysroot-build/dotnet64-v2}")"
[[ -f "$prefix/lib/libc.a" ]] || { printf 'Build musl first: scripts/build-portable-libc.sh %s\n' "$prefix" >&2; exit 1; }
llvm="$("$root/scripts/fetch-sources.sh" --path llvm-project)"
bash "$root/scripts/ensure-built.sh"
build="$prefix/build-cxx"
headers="$prefix/include/c++/v1"
rm -rf "${build:?}" "${headers:?}"
mkdir -p "$build" "$headers"
cp -a "$llvm/libcxx/include/." "$headers/"
rm -f "$headers/__config_site.in" "$headers/CMakeLists.txt"
cp "$llvm/libcxx/vendor/llvm/default_assertion_handler.in" "$headers/__assertion_handler"
cp "$llvm/libcxxabi/include/"*.h "$headers/"
# __config_site for musl on dotnet64-v2: Itanium ABI v1, pthreads, localization, filesystem, no time zone database.
sed -E \
    -e 's|^#cmakedefine _LIBCPP_ABI_VERSION .*|#define _LIBCPP_ABI_VERSION 1|' \
    -e 's|^#cmakedefine _LIBCPP_ABI_NAMESPACE .*|#define _LIBCPP_ABI_NAMESPACE __1|' \
    -e 's|^#cmakedefine01 (_LIBCPP_HAS_THREADS\|_LIBCPP_HAS_MONOTONIC_CLOCK\|_LIBCPP_HAS_TERMINAL\|_LIBCPP_HAS_MUSL_LIBC\|_LIBCPP_HAS_THREAD_API_PTHREAD\|_LIBCPP_HAS_FILESYSTEM\|_LIBCPP_HAS_RANDOM_DEVICE\|_LIBCPP_HAS_LOCALIZATION\|_LIBCPP_HAS_UNICODE\|_LIBCPP_HAS_WIDE_CHARACTERS)$|#define \1 1|' \
    -e 's|^#cmakedefine01 (.*)$|#define \1 0|' \
    -e 's|^#cmakedefine _LIBCPP_PSTL_BACKEND_SERIAL$|#define _LIBCPP_PSTL_BACKEND_SERIAL|' \
    -e 's|^#cmakedefine _LIBCPP_HARDENING_MODE_DEFAULT .*|#define _LIBCPP_HARDENING_MODE_DEFAULT 2|' \
    -e 's|^#cmakedefine _LIBCPP_ASSERTION_SEMANTIC_DEFAULT .*|#define _LIBCPP_ASSERTION_SEMANTIC_DEFAULT 2|' \
    -e 's|^#cmakedefine .*$||' -e 's|^@_LIBCPP_.*_DEFINES@$||' \
    "$llvm/libcxx/include/__config_site.in" >"$headers/__config_site"
cxx_sources=(algorithm any bind call_once charconv chrono error_category exception expected
    filesystem/filesystem_clock filesystem/filesystem_error filesystem/path functional hash memory memory_resource
    new_handler new_helpers optional print random_shuffle ryu/d2fixed ryu/d2s ryu/f2s stdexcept string system_error
    typeinfo valarray variant vector verbose_abort atomic barrier condition_variable_destructor condition_variable
    future mutex_destructor mutex shared_mutex thread random fstream ios ios.instantiations iostream locale ostream
    regex strstream filesystem/directory_entry filesystem/directory_iterator filesystem/operations filesystem/int128_builtins)
abi_sources=(abort_message cxa_aux_runtime cxa_demangle cxa_guard cxa_vector cxa_virtual cxa_thread_atexit
    fallback_malloc private_typeinfo stdlib_exception stdlib_new_delete stdlib_stdexcept stdlib_typeinfo llvmnet-catch)
# The libraries see the platform macros user code sees (ADR-14): libc++'s headers choose ABI details such as the
# atomic-wait contention type by __linux__, so a library built with it would not match programs built without it.
# Only cxa_thread_atexit.cpp, which defines __cxa_thread_atexit solely under __linux__, gets the macro.
common=(--runtime=portable --portable-sysroot "$prefix" -c -O2 -std=c++23 -nostdinc++ -isystem "$headers"
    -fvisibility-inlines-hidden -Wno-everything -D_LIBCPP_BUILDING_LIBRARY -D_LIBCPP_REMOVE_TRANSITIVE_INCLUDES)
printf '%s\n' "${common[@]}" -I"$llvm/libcxx/src" -I"$llvm/libc" -DLIBCXX_BUILDING_LIBCXXABI -D_LIBCPP_DISABLE_NEW_DELETE_DEFINITIONS >"$build/cxx-flags.txt"
printf '%s\n' "${common[@]}" -I"$llvm/libcxxabi/include" -I"$llvm/libcxxabi/src" -I"$llvm/libcxx/src" -D_LIBCXXABI_BUILDING_LIBRARY \
    -DHAVE___CXA_THREAD_ATEXIT_IMPL >"$build/abi-flags.txt"
export LLVMNET_SKIP_BUILD=1 build root llvm
compile() {
    local library="$1" name="$2" source object
    if [[ "$library" == cxx ]]; then source="$llvm/libcxx/src/$name.cpp"; else source="$llvm/libcxxabi/src/$name.cpp"; fi
    [[ "$name" == llvmnet-* ]] && source="$root/ports/libcxxabi/$name.cpp"
    object="$build/$library/$name.o"
    mkdir -p "$(dirname "$object")"
    mapfile -t flags <"$build/$library-flags.txt"
    [[ "$name" == cxa_thread_atexit ]] && flags+=(-D__linux__)
    if ! "$root/bin/llvmnet" "${flags[@]}" "$source" -o "$object" >"$object.log" 2>&1; then
        printf '%s/%s\t%s\n' "$library" "$name" "$(grep -m1 -E 'error' "$object.log" | sed "s|$llvm/||" | tr '\t' ' ')" >>"$build/failures.tsv"
        rm -f "$object"
    fi
}
export -f compile
: >"$build/failures.tsv"
{ printf 'cxx %s\n' "${cxx_sources[@]}"; printf 'abi %s\n' "${abi_sources[@]}"; } |
    xargs -P "${JOBS:-$(nproc)}" -L 1 bash -c 'compile "$1" "$2"' _
sort -o "$build/failures.tsv" "$build/failures.tsv"
for library in cxx abi; do
    mapfile -t objects < <(find "$build/$library" -name '*.o' | sort)
    archive="$prefix/lib/libc++.a"; [[ "$library" == abi ]] && archive="$prefix/lib/libc++abi.a"
    rm -f "$archive"
    llvm-ar-22 rcs "$archive" "${objects[@]}"
done
printf 'libc++/libc++abi for dotnet64-v2: %d of %d sources compiled; failures in %s\n' \
    "$(find "$build" -name '*.o' | wc -l)" "$(( ${#cxx_sources[@]} + ${#abi_sources[@]} ))" "$build/failures.tsv"
