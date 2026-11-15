#!/usr/bin/env bash
# Build the LLVM tools llvmnet runs as portable JIT images in the tree configure.sh made, and stage them as a toolchain's
# libexec/llvm for package-toolchain.sh --portable: for every name the compiler and the bin/ commands call, a launcher
# (a POSIX script and a .cmd, llvmnet --write-launcher) that runs the tool's payload; Clang's resource headers; and Flang's
# intrinsic modules, made by the built flang as flang/tools/f18 makes them natively (a cross build does not).
# Usage: build-tools.sh [stage directory (default <work>/stage)]
#   FLANG_MODULES  a directory of Flang 22 .mod files to use instead of making them
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
stage="$(realpath -m "${1:-$work/stage}")"
[[ -f "$build_dir/build.ninja" ]] || bash "$selfhost/configure.sh"
ninja -C "$build_dir" -j"$jobs" clang flang opt llvm-ar llvm-nm
rm -rf "$stage"
mkdir -p "$stage/bin" "$stage/lib/clang" "$stage/include/flang"
# tool <image in the build's bin/> <names...>: the payload once, a launcher per name.
tool() {
    local image="$1" name
    shift
    cp -a "$build_dir/bin/$image.llvmnet" "$stage/bin/$image.llvmnet"
    for name in "$@"; do
        "$toolchain/bin/llvmnet" --write-launcher="$stage/bin/$image.llvmnet/program.dll" -o "$stage/bin/$name"
    done
}
tool clang-22 clang-22 clang++-22 clang clang++
tool flang-22 flang-22 flang
tool opt opt-22 opt
tool llvm-ar llvm-ar-22 llvm-ranlib-22 llvm-ar llvm-ranlib
tool llvm-nm llvm-nm-22 llvm-nm
cp -a "$build_dir/lib/clang/22" "$stage/lib/clang/"
if [[ -n "${FLANG_MODULES:-}" ]]; then
    cp "$FLANG_MODULES"/*.mod "$stage/include/flang/"
else
    modules="$source_dir/flang/module"
    generate() { "$stage/bin/flang" "$@" -cpp -fsyntax-only -module-dir "$stage/include/flang"; }
    for module in __fortran_builtins __fortran_type_info __fortran_ieee_exceptions iso_fortran_env_impl iso_c_binding \
        iso_fortran_env ieee_exceptions ieee_arithmetic ieee_features flang_debug; do
        generate "$modules/$module.f90"
    done
    generate -fc1 -xcuda "$modules/__cuda_builtins.f90"
    for module in __cuda_device cudadevice cooperative_groups; do generate -fc1 -xcuda "$modules/$module.f90"; done
fi
printf 'Staged the llvmnet-built LLVM tools in %s (%s, %d Flang modules)\n' "$stage" "$(du -sh "$stage" | cut -f1)" "$(find "$stage/include/flang" -name '*.mod' | wc -l)"
