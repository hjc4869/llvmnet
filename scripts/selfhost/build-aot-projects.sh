#!/usr/bin/env bash
# Link the LLVM tools llvmnet runs for NativeAOT (XP-16): for each tool that build-tools.sh links as a JIT image, the
# NativeAOT project of the same objects linked with --aot-project, from which "scripts/aot-tools.sh publish" makes the
# tool's executable of a precompiled package on a host of each operating system. The compiler translates differently for
# NativeAOT: the image has no type initializer that every method would test before it reads a global, calls function
# pointers without the test for a fat pointer and keeps stack allocations in frame slots (PF-24, PF-26, PF-27). An
# executable published from a JIT image (aot-tools.sh projects) runs the same code without these, and compiles LLVM's
# sources in about 1.15 times the time of this one (docs/evidence/2026-11-16-bootstrap-performance.md).
# The projects are relocatable, and hold the runtime of this checkout as the JIT images do.
# Usage: build-aot-projects.sh [projects directory (default <work>/aot-projects)]
#   LLVMNET_SELFHOST_LIBRARY  in a bootstrap build, the LlvmNet.Llvm.dll this checkout's compiler links on (default
#                             <work>/library/LlvmNet.Llvm.dll, which build-library.sh makes)
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
projects="$(realpath -m "${1:-$work/aot-projects}")"
[[ -f "$build_dir/build.ninja" ]] || bash "$selfhost/configure.sh"
settle_sysroot_headers
# The same objects as the JIT images: nothing to compile after build-tools.sh.
mapfile -t inputs < <(for image in "${tool_images[@]}"; do link_inputs "$image"; done | sort -u)
ninja -C "$build_dir" -j"$jobs" "${inputs[@]}"
library="$(realpath -m "${LLVMNET_SELFHOST_LIBRARY:-$work/library/LlvmNet.Llvm.dll}")"
if [[ "$bootstrap" == 1 && ! -f "$library" ]]; then printf 'No %s; build it with build-library.sh first.\n' "$library" >&2; exit 1; fi
link_environment "$library"
rm -rf "$projects"
mkdir -p "$projects"
for image in "${tool_images[@]}"; do
    link_with_checkout "$image" "$projects/$(basename "$image")"
    [[ -f "$projects/$(basename "$image")/NativeHost.csproj" ]] || { printf 'The link of %s wrote no NativeAOT project\n' "$image" >&2; exit 1; }
    printf 'Linked %s for NativeAOT\n' "$image"
done
printf 'Wrote the NativeAOT projects of the LLVM tools in %s (%s)\n' "$projects" "$(du -sh "$projects" | cut -f1)"
