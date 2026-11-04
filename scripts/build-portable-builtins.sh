#!/usr/bin/env bash
# Build the compiler-rt builtins that Clang calls for portable code but that a C library does not provide: complex
# multiplication and division with C Annex G semantics (__mulsc3, __muldc3, __mulxc3 and the matching divisions), the
# long double integer power __powixf2, which the linker substitutes for llvm.powi.f80, and the x86 CPU model behind
# __builtin_cpu_supports, __builtin_cpu_is and target_clones (__cpu_model, __cpu_features2, __cpu_indicator_init),
# whose CPUID and XGETBV queries portable code answers through the runtime (SV-12). llvmnet lowers the other
# arithmetic builtins itself. With --system, the CPU model alone for system mode, whose native libraries provide the
# rest; its queries run in native thunks.
#   scripts/build-portable-builtins.sh [--system] [prefix]   default artifacts/sysroot-build/dotnet64-v2 (must contain musl)
# Installs <prefix>/lib/libclang_rt.builtins.a; a step of scripts/build-sysroot.sh.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
system=0
[[ "${1:-}" == --system ]] && { system=1; shift; }
prefix="$(realpath -m "${1:-$root/artifacts/sysroot-build/dotnet64-v2}")"
builtins="$("$root/scripts/fetch-sources.sh" --path llvm-project)/compiler-rt/lib/builtins"
build="$prefix/build-builtins"
rm -rf "${build:?}"
mkdir -p "$build" "$prefix/lib"
if [[ "$system" -eq 1 ]]; then
    LLVMNET_SKIP_BUILD=1 "$root/bin/llvmnet" --runtime=system -c -O2 -std=c11 -Wno-everything -I"$builtins" \
        "$builtins/cpu_model/x86.c" -o "$build/cpu_model.o"
    rm -f "$prefix/lib/libclang_rt.builtins.a"
    llvm-ar-22 rcs "$prefix/lib/libclang_rt.builtins.a" "$build/cpu_model.o"
    printf 'compiler-rt builtins for system-linux-x64-v1: 1 object\n'
    exit 0
fi
[[ -f "$prefix/lib/libc.a" ]] || { printf 'Build musl first: scripts/build-portable-libc.sh %s\n' "$prefix" >&2; exit 1; }
objects=()
for name in mulsc3 muldc3 mulxc3 divsc3 divdc3 divxc3 powixf2 cpu_model/x86; do
    # compiler-rt selects its x87 long double code by __x86_64__, which portable code does not see; the data layout
    # is x86-64's, and these functions expose no header ABI, so the macro is defined for them alone.
    LLVMNET_SKIP_BUILD=1 "$root/bin/llvmnet" --runtime=portable --portable-sysroot "$prefix" -c -O2 -std=c11 -Wno-everything \
        -D__x86_64__ -I"$builtins" "$builtins/$name.c" -o "$build/${name##*/}.o"
    objects+=("$build/${name##*/}.o")
done
rm -f "$prefix/lib/libclang_rt.builtins.a"
llvm-ar-22 rcs "$prefix/lib/libclang_rt.builtins.a" "${objects[@]}"
printf 'compiler-rt builtins for dotnet64-v2: %d objects\n' "${#objects[@]}"
