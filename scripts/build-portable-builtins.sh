#!/usr/bin/env bash
# Build the compiler-rt builtins that Clang calls for portable code but that a C library does not provide: complex
# multiplication and division with C Annex G semantics (__mulsc3, __muldc3, __mulxc3 and the matching divisions).
# llvmnet lowers the other arithmetic builtins itself.
#   scripts/build-portable-builtins.sh [prefix]     default artifacts/sysroot-build/dotnet64-v2 (must contain musl)
# Installs <prefix>/lib/libclang_rt.builtins.a; a step of scripts/build-sysroot.sh.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="$(realpath -m "${1:-$root/artifacts/sysroot-build/dotnet64-v2}")"
[[ -f "$prefix/lib/libc.a" ]] || { printf 'Build musl first: scripts/build-portable-libc.sh %s\n' "$prefix" >&2; exit 1; }
builtins="$("$root/scripts/fetch-sources.sh" --path llvm-project)/compiler-rt/lib/builtins"
build="$prefix/build-builtins"
rm -rf "${build:?}"
mkdir -p "$build"
objects=()
for name in mulsc3 muldc3 mulxc3 divsc3 divdc3 divxc3; do
    # compiler-rt selects its x87 long double code by __x86_64__, which portable code does not see; the data layout
    # is x86-64's, and these functions expose no header ABI, so the macro is defined for them alone.
    LLVMNET_SKIP_BUILD=1 "$root/bin/llvmnet" --runtime=portable --portable-sysroot "$prefix" -c -O2 -std=c11 -Wno-everything \
        -D__x86_64__ -I"$builtins" "$builtins/$name.c" -o "$build/$name.o"
    objects+=("$build/$name.o")
done
rm -f "$prefix/lib/libclang_rt.builtins.a"
llvm-ar-22 rcs "$prefix/lib/libclang_rt.builtins.a" "${objects[@]}"
printf 'compiler-rt builtins for dotnet64-v2: %d objects\n' "${#objects[@]}"
