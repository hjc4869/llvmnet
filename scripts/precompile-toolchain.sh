#!/usr/bin/env bash
# Precompile a portable toolchain in place for one runtime identifier (XP-04, XP-14). The LLVM tools (Clang, Flang, opt,
# llvm-ar, llvm-nm) become NativeAOT executables (scripts/aot-tools.sh) in libexec/llvm/bin in place of their payloads,
# under the names the universal package has: on Linux and macOS each tool's executable with its other names (clang++-22,
# clang, llvm-ranlib-22, ...) as symbolic links to it, as in an LLVM installation; on Windows <name>.exe with the other
# names as launcher pairs whose payload is the executable (a zip holds no links), which Flang's driver needs to find
# itself as flang for -fc1 and Clang and llvm-ar read their mode from. The compiler, LlvmNet.Llvm.dll and
# the runtime are precompiled with ReadyToRun (scripts/readytorun.sh): the compiler writes assemblies with
# System.Reflection.Emit, which NativeAOT does not provide, so it stays a .NET program that starts from precompiled code.
# The result runs on hosts of the identifier only.
# Usage: scripts/precompile-toolchain.sh <portable-toolchain-prefix> <rid>
#   LLVMNET_AOT_TOOLS  the tools already published for <rid> by "scripts/aot-tools.sh publish" on a host of its operating
#                      system; without it they are published here, which NativeAOT can do for this host's operating
#                      system only (and from Linux x64 for linux-arm64 with Debian's cross packages)
#   JOBS               assemblies precompiled at once, as for readytorun.sh
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="${1:?usage: precompile-toolchain.sh <prefix> <rid>}"
rid="${2:?usage: precompile-toolchain.sh <prefix> <rid>}"
prefix="$(realpath -m "$prefix")"
[[ -f "$prefix/lib/llvmnet/LlvmNet.Llvm.dll" ]] || { printf '%s is not a portable toolchain (no lib/llvmnet/LlvmNet.Llvm.dll).\n' "$prefix" >&2; exit 2; }
bin="$prefix/libexec/llvm/bin"
[[ -f "$bin/clang-22.llvmnet/program.dll" ]] || { printf '%s has no tool payloads to precompile (libexec/llvm/bin/clang-22.llvmnet); is it precompiled already?\n' "$prefix" >&2; exit 2; }
suffix=""
case "$rid" in win-*) suffix=".exe" ;; esac
tools="${LLVMNET_AOT_TOOLS:-}"
if [[ -z "$tools" ]]; then
    work="$(mktemp -d "${TMPDIR:-/tmp}/llvmnet-aot-tools.XXXXXX")"
    trap 'rm -rf "$work"' EXIT
    bash "$root/scripts/aot-tools.sh" projects "$bin" "$work/projects"
    bash "$root/scripts/aot-tools.sh" publish "$work/projects" "$rid" "$work/tools"
    tools="$work/tools"
fi
for tool in clang-22 flang-22 opt-22 llvm-ar-22 llvm-nm-22; do
    [[ -f "$tools/$tool$suffix" ]] || { printf 'No %s in %s\n' "$tool$suffix" "$tools" >&2; exit 1; }
done
# The tool's executable, then the other names it is run by (scripts/selfhost/build-tools.sh writes a launcher for each).
names() {
    case "$1" in
        clang-22) printf '%s\n' clang-22 clang++-22 clang clang++ ;;
        flang-22) printf '%s\n' flang-22 flang ;;
        opt-22) printf '%s\n' opt-22 opt ;;
        llvm-ar-22) printf '%s\n' llvm-ar-22 llvm-ranlib-22 llvm-ar llvm-ranlib ;;
        llvm-nm-22) printf '%s\n' llvm-nm-22 llvm-nm ;;
    esac
}
rm -rf "$bin"
mkdir -p "$bin"
for tool in clang-22 flang-22 opt-22 llvm-ar-22 llvm-nm-22; do
    cp "$tools/$tool$suffix" "$bin/$tool$suffix"
    chmod 755 "$bin/$tool$suffix"
    while read -r name; do
        [[ "$name" != "$tool" ]] || continue
        if [[ -z "$suffix" ]]; then ln -s "$tool" "$bin/$name"
        else "$prefix/bin/llvmnet" --write-launcher="$bin/$tool$suffix" -o "$bin/$name"; fi
    done < <(names "$tool")
done
# Last: the package's compiler writes the Windows launchers above, and an image precompiled for another operating system
# does not load.
bash "$root/scripts/readytorun.sh" "$rid" "$prefix"/lib/llvmnet/llvmnet.dll "$prefix"/lib/llvmnet/LlvmNet.Llvm.dll \
    "$prefix"/lib/llvmnet/LlvmNet.Runtime.dll >/dev/null
printf '%s\n' "$rid" >"$prefix/lib/llvmnet/readytorun.txt"
