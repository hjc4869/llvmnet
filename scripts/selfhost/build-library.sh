#!/usr/bin/env bash
# Build LlvmNet.Llvm.dll, the compiler's LLVM as a portable managed library (XP-04): the LLVM libraries the compiler's
# bridge needs (the components core bitreader bitwriter irreader linker object passes codegen) from the tree configure.sh
# made, llvmnet's native/*.cpp, and a table that references every C API function this checkout's compiler calls (llvmnet
# --print-llvm-imports), so the link extracts the archive members that define them. They are linked with
# -shared --embed-libc: the exports are LLVM's C API (LLVM_C_ABI, default visibility) and the bridge's llvmnet_*
# functions; LLVM's C++ and the C library stay internal.
#
# The library is linked by this checkout's compiler. In a bootstrap build (common.sh) that compiler needs an in-process
# LLVM with this checkout's bridge before there is one, so the portable toolchain first links the same inputs into
# <out>/stage1/LlvmNet.Llvm.dll, and this checkout's compiler links the library on that.
# Usage: build-library.sh [output directory (default <work>/library)]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
out="$(realpath -m "${1:-$work/library}")"
[[ -f "$build_dir/build.ninja" ]] || bash "$selfhost/configure.sh"
settle_sysroot_headers
# The libraries of those components and everything they link, each before the ones it needs, as llvm-config
# --link-static --libs lists them: from the build tree's CMake exports, since a bootstrap build has no llvm-config that
# can answer for this tree.
mapfile -t libraries < <(python3 - "$build_dir/lib/cmake/llvm/LLVMExports.cmake" LLVMCore LLVMBitReader LLVMBitWriter \
    LLVMIRReader LLVMLinker LLVMObject LLVMPasses LLVMCodeGen <<'PYTHON'
import re, sys
text = open(sys.argv[1]).read()
dependencies = {}
for name, value in re.findall(r'set_target_properties\((LLVM\w+) PROPERTIES\s+INTERFACE_LINK_LIBRARIES "([^"]*)"', text):
    items = [re.sub(r'^\$<LINK_ONLY:(.*)>$', r'\1', item) for item in value.split(';')]
    dependencies.setdefault(name, [item for item in items if re.fullmatch(r'LLVM\w+', item)])
order, seen = [], set()
def visit(name):
    if name in seen: return
    seen.add(name)
    for dependency in dependencies.get(name, []): visit(dependency)
    order.append(name)
for root in sys.argv[2:]: visit(root)
print('\n'.join(reversed(order)))
PYTHON
)
[[ ${#libraries[@]} -gt 0 ]] || { printf 'No LLVM libraries found in %s/lib/cmake/llvm/LLVMExports.cmake\n' "$build_dir" >&2; exit 1; }
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
mapfile -t imports < <("$root/bin/llvmnet" --print-llvm-imports | grep '^LLVM')
{
    printf 'extern void %s(void);\n' "${imports[@]}"
    printf '__attribute__((visibility("hidden"), used)) void (*const llvmnet_llvm_roots[])(void) = {\n'
    printf '    %s,\n' "${imports[@]}"
    printf '};\n'
} >"$obj/roots.c"
"$toolchain/bin/llvmnet-clang" -O2 -c "$obj/roots.c" -o "$obj/roots.o"
archives=()
for library in "${libraries[@]}"; do archives+=("$build_dir/lib/lib$library.a"); done
inputs=(-O2 -shared --embed-libc "${objects[@]}" "$obj/roots.o" "${archives[@]}")
if [[ "$bootstrap" == 1 ]]; then
    mkdir -p "$out/stage1"
    "$toolchain/bin/llvmnet-clang++" "${inputs[@]}" -o "$out/stage1/LlvmNet.Llvm.dll"
    printf 'Linked the first library with %s\n' "$toolchain"
fi
link_environment "$out/stage1/LlvmNet.Llvm.dll"
env ${link_env[@]+"${link_env[@]}"} "$root/bin/llvmnet-clang++" "${inputs[@]}" -o "$out/LlvmNet.Llvm.dll"
printf 'Built %s (%s)\n' "$out/LlvmNet.Llvm.dll" "$(du -h "$out/LlvmNet.Llvm.dll" | cut -f1)"
