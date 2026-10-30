#!/usr/bin/env bash
# Build the portable sysroot sysroot/dotnet64-v2 (plan items LC-15, CX-07, FT-05): unmodified musl 1.2.5,
# libc++/libc++abi and flang-rt 22.1.8 compiled to LLVM bitcode by llvmnet, with their headers; and the system-mode
# sysroot sysroot/system-linux-x64-v1, which holds flang-rt built against the host's glibc (FT-09).
#   scripts/build-sysroot.sh [--force]
# The sysroot is rebuilt only when an input changes: the pinned sources, ports/, these build scripts, the
# driver code that chooses portable compile flags, or the Clang version. Build trees stay in
# artifacts/sysroot-build/dotnet64-v2; the script fails if any library source does not compile.
# Needs a built compiler (scripts/ensure-built.sh runs this script after building the solution).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
force=0
[[ "${1:-}" == --force ]] && force=1
sysroot="$root/sysroot/dotnet64-v2"
system_sysroot="$root/sysroot/system-linux-x64-v1"
build="$root/artifacts/sysroot-build/dotnet64-v2"
inputs="$(
    cd "$root"
    {
        { find ports -type f; printf '%s\n' scripts/build-sysroot.sh scripts/build-portable-libc.sh scripts/build-portable-cxx.sh \
            scripts/build-flang-rt.sh src/LlvmNet.Compiler/Driver.cs src/LlvmNet.Compiler/PortableAbi.cs \
            src/LlvmNet.Compiler/Llvm.cs native/abi.cpp; } | LC_ALL=C sort | xargs sha256sum
        grep -E '^(musl|llvm-project)\s' scripts/sources.lock
        clang-22 --version | head -n 1
    } | sha256sum | cut -d' ' -f1
)"
if [[ "$force" -eq 0 && -f "$sysroot/.llvmnet-inputs" && "$(<"$sysroot/.llvmnet-inputs")" == "$inputs" &&
    -f "$system_sysroot/.llvmnet-inputs" && "$(<"$system_sysroot/.llvmnet-inputs")" == "$inputs" ]]; then
    printf '%s\n' "$sysroot"
    exit 0
fi
export LLVMNET_SKIP_BUILD=1
rm -rf "${build:?}"
mkdir -p "$build"
start=$(date +%s)
steps=("libc:build-portable-libc.sh $build" "cxx:build-portable-cxx.sh $build" "flang-rt:build-flang-rt.sh portable $build"
    "flang-rt-system:build-flang-rt.sh system $build/system")
for step in "${steps[@]}"; do
    name="${step%%:*}"
    read -r -a command <<<"${step#*:}"
    if ! bash "$root/scripts/${command[0]}" "${command[@]:1}" >"$build/$name.log" 2>&1; then
        tail -n 40 "$build/$name.log" >&2
        printf 'FAIL: sysroot step %s failed; log: %s\n' "$name" "$build/$name.log" >&2
        exit 1
    fi
done
failed="$(cat "$build/build/failures.tsv" "$build/build-cxx/failures.tsv" "$build/build-flang-rt/failures.tsv" \
    "$build/system/build-flang-rt/failures.tsv")"
if [[ -n "$failed" ]]; then
    printf 'FAIL: library sources did not compile for the portable sysroot:\n%s\n' "$failed" >&2
    exit 1
fi
# Install into a fresh directory and swap it in, so a concurrent reader never sees a partial sysroot.
stage="$sysroot.new"
rm -rf "${stage:?}"
mkdir -p "$stage/lib" "$stage/share/licenses"
cp -a "$build/include" "$stage/include"
cp "$build"/lib/*.a "$build/lib/crt1.o" "$stage/lib/"
cp "$build/COPYRIGHT" "$stage/share/licenses/musl.COPYRIGHT"
llvm="$("$root/scripts/fetch-sources.sh" --path llvm-project)"
cp "$llvm/libcxx/LICENSE.TXT" "$stage/share/licenses/libcxx.LICENSE.TXT"
cp "$llvm/libcxxabi/LICENSE.TXT" "$stage/share/licenses/libcxxabi.LICENSE.TXT"
cp "$llvm/flang-rt/LICENSE.TXT" "$stage/share/licenses/flang-rt.LICENSE.TXT" 2>/dev/null ||
    cp "$llvm/flang/LICENSE.TXT" "$stage/share/licenses/flang-rt.LICENSE.TXT"
printf '%s\n' "$inputs" >"$stage/.llvmnet-inputs"
# Objects compiled against the sysroot depend only on its headers: builds of other projects key on this hash.
(cd "$stage/include" && find . -type f | LC_ALL=C sort | xargs sha256sum | sha256sum | cut -d' ' -f1) >"$stage/.llvmnet-headers"
rm -rf "${sysroot:?}.old"
[[ -d "$sysroot" ]] && mv "$sysroot" "$sysroot.old"
mv "$stage" "$sysroot"
rm -rf "${sysroot:?}.old"
system_stage="$system_sysroot.new"
rm -rf "${system_stage:?}"
mkdir -p "$system_stage/lib" "$system_stage/share/licenses"
cp "$build/system/lib/libflang_rt.runtime.a" "$system_stage/lib/"
cp "$sysroot/share/licenses/flang-rt.LICENSE.TXT" "$system_stage/share/licenses/"
printf '%s\n' "$inputs" >"$system_stage/.llvmnet-inputs"
rm -rf "${system_sysroot:?}.old"
[[ -d "$system_sysroot" ]] && mv "$system_sysroot" "$system_sysroot.old"
mv "$system_stage" "$system_sysroot"
rm -rf "${system_sysroot:?}.old"
printf 'Built sysroots %s and %s in %d s (%s)\n' "$sysroot" "$system_sysroot" "$(( $(date +%s) - start ))" \
    "$(tail -n 1 "$build/libc.log"); $(tail -n 1 "$build/cxx.log"); $(tail -n 1 "$build/flang-rt.log"); $(tail -n 1 "$build/flang-rt-system.log")" >&2
printf '%s\n' "$sysroot"
