#!/usr/bin/env bash
# Build LlvmNet.Llvm.dll, the compiler's LLVM as a portable managed library (XP-04): the LLVM libraries the compiler's
# bridge needs (llvm-config's core bitreader bitwriter irreader linker object passes codegen) from the tree configure.sh
# made, llvmnet's native/*.cpp, and a table that references every C API function the compiler calls (llvmnet
# --print-llvm-imports), so the link extracts the archive members that define them. They are linked with
# -shared --embed-libc: the exports are LLVM's C API (LLVM_C_ABI, default visibility) and the bridge's llvmnet_*
# functions; LLVM's C++ and the C library stay internal.
# Usage: build-library.sh [output directory (default <work>/library)]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
out="$(realpath -m "${1:-$work/library}")"
[[ -f "$build_dir/build.ninja" ]] || bash "$selfhost/configure.sh"
mapfile -t libraries < <(llvm-config-22 --link-static --libs core bitreader bitwriter irreader linker object passes codegen | tr ' ' '\n' | sed -n 's/^-l//p')
ninja -C "$build_dir" -j"$jobs" "${libraries[@]}"
obj="$out/obj"
mkdir -p "$obj"
flags=(-O3 -std=c++17 -fno-exceptions -fno-rtti -fvisibility=hidden -fvisibility-inlines-hidden
    -D__linux__=1 -D__linux=1 -D__gnu_linux__=1 -D__unix__=1 -D__unix=1 -D_GNU_SOURCE
    -D__STDC_CONSTANT_MACROS -D__STDC_FORMAT_MACROS -D__STDC_LIMIT_MACROS -DNDEBUG
    -I"$build_dir/include" -I"$source_dir/llvm/include")
objects=()
for source in "$root"/native/*.cpp; do
    object="$obj/$(basename "$source" .cpp).o"
    "$toolchain/bin/llvmnet-clang++" "${flags[@]}" -c "$source" -o "$object"
    objects+=("$object")
done
# Declared without their prototypes: the table only takes each function's address, and the bitcode linker resolves the
# references by name to LLVM's definitions.
{
    "$toolchain/bin/llvmnet" --print-llvm-imports | grep '^LLVM' | sed 's/.*/extern void &(void);/'
    printf '__attribute__((visibility("hidden"), used)) void (*const llvmnet_llvm_roots[])(void) = {\n'
    "$toolchain/bin/llvmnet" --print-llvm-imports | grep '^LLVM' | sed 's/.*/    &,/'
    printf '};\n'
} >"$obj/roots.c"
"$toolchain/bin/llvmnet-clang" -O2 -c "$obj/roots.c" -o "$obj/roots.o"
archives=()
for library in "${libraries[@]}"; do archives+=("$build_dir/lib/lib$library.a"); done
"$toolchain/bin/llvmnet-clang++" -O2 -shared --embed-libc "${objects[@]}" "$obj/roots.o" "${archives[@]}" -o "$out/LlvmNet.Llvm.dll"
printf 'Built %s (%s)\n' "$out/LlvmNet.Llvm.dll" "$(du -h "$out/LlvmNet.Llvm.dll" | cut -f1)"
