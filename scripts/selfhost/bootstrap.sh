#!/usr/bin/env bash
# Prepare the toolchain of a bootstrap build (XP-11, README.md) in <work>/bootstrap and print its path, for
# LLVMNET_SELFHOST_TOOLCHAIN: the release pinned as llvmnet-bootstrap in scripts/sources.lock, its universal portable
# package, precompiled for this host as a release's precompiled package is (scripts/precompile-toolchain.sh: the LLVM
# tools as NativeAOT executables, the compiler with ReadyToRun). JIT-compiled or ReadyToRun tools, which the universal
# package and the precompiled packages of releases before XP-14 have, compile LLVM's sources at three to four times the
# CPU time of NativeAOT ones. A directory prepared from the same pin is reused; the path stays the same when the pin
# changes, so the build tree keeps its objects (as after a compiler change, scripts/selfhost/README.md).
# Needs what NativeAOT needs (the .NET 10 SDK, clang or gcc, zlib's development files) and this checkout's compiler,
# which writes the tools' NativeAOT projects. Runs on Linux x64.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(realpath -m "${LLVMNET_SELFHOST_DIR:-$root/artifacts/selfhost-build}")"
target="$work/bootstrap"
case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;;
    *) printf 'A bootstrap build runs on Linux x64, which LLVM'"'"'s build in portable mode assumes (configure.sh).\n' >&2; exit 2 ;;
esac
pin="$(grep -P '^llvmnet-bootstrap\t' "$root/scripts/sources.lock")" || { printf 'No llvmnet-bootstrap in scripts/sources.lock\n' >&2; exit 1; }
stamp="$(printf '%s\n' "$pin" | sha256sum | cut -d' ' -f1)"
if [[ -f "$target/.llvmnet-bootstrap" && "$(<"$target/.llvmnet-bootstrap")" == "$stamp" ]]; then
    printf '%s\n' "$target"
    exit 0
fi
universal="$(bash "$root/scripts/fetch-sources.sh" --path llvmnet-bootstrap)"
[[ -f "$universal/lib/llvmnet/LlvmNet.Llvm.dll" && -f "$universal/libexec/llvm/bin/clang-22.llvmnet/program.dll" ]] ||
    { printf '%s is not a universal portable package (lib/llvmnet/LlvmNet.Llvm.dll, libexec/llvm/bin/clang-22.llvmnet).\n' "$universal" >&2; exit 1; }
rm -rf "$target"
mkdir -p "$work"
cp -a "$universal" "$target"
bash "$root/scripts/precompile-toolchain.sh" "$target" "$rid" >&2
printf '%s\n' "$stamp" >"$target/.llvmnet-bootstrap"
printf 'Prepared the bootstrap toolchain %s in %s\n' "$(cut -f2 <<<"$pin")" "$target" >&2
printf '%s\n' "$target"
