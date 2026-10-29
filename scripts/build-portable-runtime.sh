#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
archive="$root/sysroot/dotnet64-v1/lib/libcxx-managed.a"
compiler="$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll"
bash "$root/scripts/install-libcxx-headers.sh" >/dev/null
if [[ "${LLVMNET_SKIP_BUILD:-0}" != 1 ]]; then dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo -v quiet -clp:ErrorsOnly; fi
sources=("$root/sysroot/dotnet64-v1/src/cxx-runtime.cpp" "$root/sysroot/dotnet64-v1/src/cxx-new.cpp")
if [[ -f "$archive" && "${LLVMNET_FORCE_PORTABLE_RUNTIME:-0}" != 1 ]]; then
    stale=0
    for input in "${sources[@]}" "$compiler" "${BASH_SOURCE[0]}"; do
        if [[ "$input" -nt "$archive" ]]; then stale=1; fi
    done
    if [[ "$stale" -eq 0 && -z "$(find "$root/sysroot/dotnet64-v1/include" -newer "$archive" -type f -not -path '*/c++/*' -print -quit)" ]]; then
        exit 0
    fi
fi
work="$root/artifacts/portable-runtime"
mkdir -p "$work" "$(dirname "$archive")"
objects=()
for source in "${sources[@]}"; do
    object="$work/$(basename "${source%.cpp}").o"
    "$root/bin/llvmnet" --runtime=portable -std=c++17 -O1 -c "$source" -o "$object"
    objects+=("$object")
done
# Replace the archive atomically so concurrent portable links never read a partial file.
llvm-ar-22 rcs "$work/libcxx-managed.a.new" "${objects[@]}"
mv "$work/libcxx-managed.a.new" "$archive"
printf 'Built portable libc++ support archive as ABI-tagged LLVM bitcode.\n'
