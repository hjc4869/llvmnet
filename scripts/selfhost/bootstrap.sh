#!/usr/bin/env bash
# Prepare the toolchain of a bootstrap build (XP-11, README.md) in <work>/bootstrap and print its path, for
# LLVMNET_SELFHOST_TOOLCHAIN: the release pinned as llvmnet-bootstrap in scripts/sources.lock. The pin is the release's
# portable package precompiled for linux-x64, whose LLVM tools are NativeAOT executables (XP-14), and is used as it is.
# A universal portable package is precompiled for this host first, as a release's precompiled package is made
# (scripts/precompile-toolchain.sh, about 15 minutes on 4 cores), which needs what NativeAOT needs: the .NET 10 SDK,
# clang or gcc, zlib's development files. JIT-compiled or ReadyToRun tools compile LLVM's sources at three to four times
# the CPU time of NativeAOT ones, so a package precompiled before XP-14, which has ReadyToRun tools, is refused.
# A directory prepared from the same pin is reused; the path stays the same when the pin changes, so the build tree keeps
# its objects (as after a compiler change, scripts/selfhost/README.md). Runs on Linux x64.
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
package="$(bash "$root/scripts/fetch-sources.sh" --path llvmnet-bootstrap)"
bin="$package/libexec/llvm/bin"
[[ -f "$package/lib/llvmnet/LlvmNet.Llvm.dll" ]] ||
    { printf '%s is not a portable llvmnet toolchain (lib/llvmnet/LlvmNet.Llvm.dll).\n' "$package" >&2; exit 1; }
if [[ -f "$package/lib/llvmnet/readytorun.txt" ]]; then
    # Precompiled: for this host, and with NativeAOT executables in place of the tools' payloads.
    precompiled="$(<"$package/lib/llvmnet/readytorun.txt")"
    [[ "$precompiled" == "$rid" ]] || { printf '%s is precompiled for %s, not %s.\n' "$package" "$precompiled" "$rid" >&2; exit 1; }
    [[ ! -e "$bin/clang-22.llvmnet" ]] ||
        { printf '%s has ReadyToRun LLVM tools (a release before XP-14); pin its universal package instead.\n' "$package" >&2; exit 1; }
    [[ -x "$bin/clang-22" && -x "$bin/flang-22" && -x "$bin/opt-22" ]] ||
        { printf '%s has no NativeAOT clang-22, flang-22 and opt-22 in libexec/llvm/bin.\n' "$package" >&2; exit 1; }
    precompile=0
elif [[ -f "$bin/clang-22.llvmnet/program.dll" ]]; then
    precompile=1
else
    printf '%s is neither a universal portable package (libexec/llvm/bin/clang-22.llvmnet) nor one precompiled for %s.\n' "$package" "$rid" >&2
    exit 1
fi
rm -rf "$target"
mkdir -p "$work"
cp -a "$package" "$target"
if [[ "$precompile" == 1 ]]; then bash "$root/scripts/precompile-toolchain.sh" "$target" "$rid" >&2; fi
printf '%s\n' "$stamp" >"$target/.llvmnet-bootstrap"
printf 'Prepared the bootstrap toolchain %s in %s\n' "$(cut -f2 <<<"$pin")" "$target" >&2
printf '%s\n' "$target"
