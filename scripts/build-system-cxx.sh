#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_root="$(realpath "${1:?Usage: build-system-cxx.sh libstdc++-v3-source [output-directory]}")"
output="$(realpath -m "${2:-$root/artifacts/system-cxx}")"
if [[ ! -f "$source_root/src/c++98/ios_init.cc" ]]; then
    printf 'Expected an upstream libstdc++-v3 source tree: %s\n' "$source_root" >&2
    exit 2
fi
mkdir -p "$output"
build="$(mktemp -d "$output/build.XXXXXXXX")"
mkdir -p "$build/include/bits"
ln -s "$source_root/config/locale/gnu/c++locale_internal.h" "$build/include/bits/c++locale_internal.h"
clang++-22 -dM -E -x c++ -include bits/c++config.h /dev/null > "$build/header-macros.txt"
if ! grep -qx '#define _GLIBCXX_RELEASE 16' "$build/header-macros.txt"; then
    printf 'This source profile requires matching libstdc++ 16 headers.\n' >&2
    exit 2
fi
awk '/^#define (_DARWIN_USE_64_BIT_INODE|_FILE_OFFSET_BITS|_LARGE_FILES)( |$)/' \
    "$build/header-macros.txt" > "$build/include/bits/largefile-config.h"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
sources=(
    src/c++98/globals_io.cc
    src/c++98/ios_init.cc
    src/c++98/ios_locale.cc
    src/c++98/istream.cc
    src/c++98/compatibility.cc
    src/c++98/istream-string.cc
    src/c++98/cow-istream-string.cc
    src/c++98/streambuf.cc
    src/c++98/misc-inst.cc
    src/c++98/tree.cc
    src/c++98/list.cc
    src/c++98/list-aux.cc
    src/c++98/list-aux-2.cc
    src/c++11/ios.cc
    src/c++11/ios-inst.cc
    src/c++11/ostream-inst.cc
    src/c++11/istream-inst.cc
    src/c++11/iostream-inst.cc
    src/c++11/streambuf-inst.cc
    src/c++11/sstream-inst.cc
    src/c++11/fstream-inst.cc
    src/c++11/string-inst.cc
    src/c++11/string-io-inst.cc
    src/c++11/wstring-inst.cc
    src/c++11/wstring-io-inst.cc
    src/c++11/cow-string-inst.cc
    src/c++11/cow-string-io-inst.cc
    src/c++11/cow-wstring-inst.cc
    src/c++11/cow-wstring-io-inst.cc
    src/c++11/ext11-inst.cc
    src/c++11/functexcept.cc
    src/c++11/functional.cc
    src/c++11/shared_ptr.cc
    src/c++11/regex.cc
    src/c++11/random.cc
    src/c++17/fs_dir.cc
    src/c++17/fs_ops.cc
    src/c++17/fs_path.cc
    src/c++11/hashtable_c++0x.cc
    src/c++11/thread.cc
    src/c++11/future.cc
    src/c++11/futex.cc
    src/c++11/condition_variable.cc
    src/c++11/mutex.cc
    src/c++11/chrono.cc
    libsupc++/hash_bytes.cc
    src/c++11/snprintf_lite.cc
    src/c++98/ios_failure.cc
    src/c++98/stdexcept.cc
    src/c++11/cow-stdexcept.cc
    src/c++11/cxx11-stdexcept.cc
    src/c++11/cxx11-ios_failure.cc
    src/c++11/ios_errcat.cc
    src/c++11/system_error.cc
    src/c++11/cow-system_error.cc
    libsupc++/eh_exception.cc
    libsupc++/bad_alloc.cc
    libsupc++/bad_array_new.cc
    libsupc++/bad_cast.cc
    libsupc++/bad_typeid.cc
    src/c++98/locale.cc
    src/c++98/locale_facets.cc
    src/c++98/codecvt.cc
    src/c++11/locale_init.cc
    src/c++11/cow-locale_init.cc
    src/c++11/cow-shim_facets.cc
    src/c++11/cxx11-shim_facets.cc
    src/c++11/sso_string.cc
    src/c++11/localename.cc
    src/c++11/locale-inst.cc
    src/c++11/wlocale-inst.cc
    src/c++11/cxx11-locale-inst.cc
    src/c++11/cxx11-wlocale-inst.cc
    src/c++11/ctype.cc
    src/c++11/codecvt.cc
    config/locale/gnu/c_locale.cc
    config/locale/gnu/codecvt_members.cc
    config/locale/gnu/collate_members.cc
    config/locale/gnu/ctype_members.cc
    config/locale/gnu/messages_members.cc
    config/locale/gnu/monetary_members.cc
    config/locale/gnu/numeric_members.cc
    config/locale/gnu/time_members.cc
    config/locale/gnu/collate_members.cc:cow
    config/locale/gnu/messages_members.cc:cow
    config/locale/gnu/monetary_members.cc:cow
    config/locale/gnu/numeric_members.cc:cow
    config/os/gnu-linux/ctype_configure_char.cc
    config/io/basic_file_stdio.cc
    ../libiberty/cp-demangle.c
)
objects=()
for source_spec in "${sources[@]}"; do
    source="${source_spec%:cow}"
    standard=gnu++11
    case "$source" in src/c++98/*|config/locale/gnu/*) standard=gnu++98 ;; esac
    if [[ "$source" == config/locale/gnu/ctype_members.cc ]]; then standard=gnu++11; fi
    if [[ "$source" == src/c++17/* ]]; then standard=gnu++17; fi
    compiler_args=()
    if [[ "$standard" == gnu++11 ]]; then compiler_args+=(-fchar8_t); fi
    if [[ "$source_spec" == *:cow ]]; then compiler_args+=(-D_GLIBCXX_USE_CXX11_ABI=0); fi
    if [[ "$source" == src/c++11/random.cc ]]; then compiler_args+=(-include "$root/config/system-cxx-random.h"); fi
    if [[ "$source" == *.c ]]; then
        standard=gnu11
        compiler_args=(-DIN_GLIBCPP_V3 -DHAVE_STDLIB_H=1 -DHAVE_STRING_H=1 -DHAVE_LIMITS_H=1 -DHAVE_ALLOCA_H=1
            -I"$source_root/../include" -I"$source_root/../libiberty")
    fi
    object="$build/${source_spec//\//_}.o"
    "$root/bin/llvmnet" --runtime=system -std="$standard" -O2 -fPIC "${compiler_args[@]}" \
        -Wno-init-priority-reserved -Wno-unknown-warning-option \
        -I"$build/include" -I"$source_root/libsupc++" -I"$source_root/src" -I"$source_root/src/c++11" \
        -c "$source_root/$source" -o "$object"
    objects+=("$object")
    sha256sum "$source_root/$source" >> "$build/sources.sha256"
done
llvm-ar-22 rcs "$build/libstdcxx-system.a" "${objects[@]}"
cp "$build/libstdcxx-system.a" "$output/libstdcxx-system.a"
printf 'Built %s upstream libstdc++ units as system-ABI bitcode: %s\n' "${#objects[@]}" "$output/libstdcxx-system.a"