# shellcheck shell=bash disable=SC2034
# Settings shared by the scripts that build LLVM, Clang and Flang 22 with llvmnet (scripts/selfhost/README.md).
#   LLVMNET_SELFHOST_DIR        work directory: the patched source copy and the build tree (default artifacts/selfhost-build)
#   LLVMNET_SELFHOST_TOOLCHAIN  the llvmnet toolchain that compiles LLVM (default this checkout: bin/, cmake/); a portable
#                               toolchain, such as the release pinned as llvmnet-bootstrap in scripts/sources.lock, makes
#                               this a bootstrap build (below)
#   NATIVE_LLVM                 directory of the host's LLVM 22 tools for TableGen when this checkout compiles LLVM
#                               (default llvm-config-22 --bindir)
#   JOBS                        parallel compile jobs (default nproc)
#
# The library and the tools are always linked, and so translated to CIL, by this checkout's compiler, so they match the
# runtime and the compiler they ship with. When the compiling toolchain is this checkout, that compiler runs on the host's
# LLVM 22 throughout. In a bootstrap build every piece of LLVM comes from the portable toolchain instead (bootstrap.sh
# prepares one): its llvmnet compiles LLVM's sources, its clang compiles the host tools of LLVM's NATIVE sub-build
# (TableGen) as native programs, and this checkout's compiler links on its clang, flang and opt and on a first
# LlvmNet.Llvm.dll that the portable toolchain links from this checkout's bridge (build-library.sh). No LLVM of the host
# is used.
selfhost="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$selfhost/../.." && pwd)"
work="$(realpath -m "${LLVMNET_SELFHOST_DIR:-$root/artifacts/selfhost-build}")"
source_dir="$work/llvm-project"
build_dir="$work/llvm"
toolchain="$(realpath -m "${LLVMNET_SELFHOST_TOOLCHAIN:-$root}")"
jobs="${JOBS:-$(nproc)}"
toolchain_file="$toolchain/cmake/llvmnet.cmake"
[[ -f "$toolchain_file" ]] || toolchain_file="$toolchain/share/llvmnet/cmake/llvmnet.cmake"
bootstrap=0
if [[ "$toolchain" != "$root" ]]; then
    if [[ ! -f "$toolchain/lib/llvmnet/LlvmNet.Llvm.dll" || ! -e "$toolchain/libexec/llvm/bin/clang-22" ]]; then
        printf '%s is not a portable llvmnet toolchain (lib/llvmnet/LlvmNet.Llvm.dll, libexec/llvm/bin/clang-22).\n' "$toolchain" >&2
        exit 2
    fi
    bootstrap=1
    llvm_bin="$toolchain/libexec/llvm/bin"
    native_llvm=""
else
    native_llvm="${NATIVE_LLVM:-$(llvm-config-22 --bindir 2>/dev/null || true)}"
fi

# The environment in which this checkout's compiler links: in a bootstrap build, on the portable toolchain's clang, flang
# and opt and on the LlvmNet.Llvm.dll given as the first argument; otherwise on the host's LLVM (nothing to set).
link_environment() {
    link_env=()
    [[ "$bootstrap" == 1 ]] || return 0
    link_env=(LLVMNET_LLVM="$1" LLVMNET_CLANG="$llvm_bin/clang-22" LLVMNET_FLANG="$llvm_bin/flang-22" LLVMNET_OPT="$llvm_bin/opt-22")
}

# The pinned llvm-project (scripts/sources.lock) with the patches in scripts/selfhost/patches applied, copied once per
# change of the lock entry or the patches. Only the projects the build uses are copied.
prepare_source() {
    local upstream stamp
    upstream="$(bash "$root/scripts/fetch-sources.sh" --path llvm-project)"
    stamp="$( { grep -P '^llvm-project\t' "$root/scripts/sources.lock"; cat "$selfhost"/patches/*.patch; } | sha256sum | cut -d' ' -f1)"
    if [[ -f "$source_dir/.llvmnet-stamp" && "$(<"$source_dir/.llvmnet-stamp")" == "$stamp" ]]; then return; fi
    rm -rf "$source_dir"
    mkdir -p "$source_dir"
    local project
    for project in llvm clang mlir flang cmake third-party; do cp -a "$upstream/$project" "$source_dir/"; done
    local patch
    for patch in "$selfhost"/patches/*.patch; do patch -s -p1 -d "$source_dir" <"$patch"; done
    printf '%s\n' "$stamp" >"$source_dir/.llvmnet-stamp"
}

# build-sysroot.sh gives the sysroot's headers new times whenever it rebuilds, also with unchanged content (and a CI run
# builds the sysroot afresh), which would make ninja recompile all of LLVM. The build tree records the header hash it was
# compiled against (the sysroot's .llvmnet-headers); while the sysroot still has that hash, its headers get the time it
# was recorded, which is older than every object compiled against them, so ninja rebuilds only what really changed.
settle_sysroot_headers() {
    local sysroot="$toolchain/sysroot/dotnet64-v2" recorded="$build_dir/.llvmnet-sysroot-headers" current
    [[ -f "$sysroot/.llvmnet-headers" && -d "$build_dir" ]] || return 0
    current="$(<"$sysroot/.llvmnet-headers")"
    if [[ -f "$recorded" && "$(<"$recorded")" == "$current" ]]; then
        find "$sysroot/include" -exec touch -h -r "$recorded" {} +
    else
        printf '%s\n' "$current" >"$recorded"
    fi
}

# The files ninja needs before it links <target>: its explicit, implicit and order-only inputs.
link_inputs() {
    ninja -C "$build_dir" -t query "$1" | awk '/^  input:/ { on = 1; next } /^  outputs:/ { on = 0 } on { sub(/^ +(\|\|? )?/, ""); print }'
}

# Links <target> of the build tree with this checkout's compiler (link_environment first): ninja's own link command for
# it, with the compiling toolchain's driver replaced by this checkout's.
link_with_checkout() {
    local target="$1" command
    command="$(ninja -C "$build_dir" -t commands "$target" | tail -n 1)"
    command="${command//"$toolchain/bin/llvmnet-clang++"/"$root/bin/llvmnet-clang++"}"
    [[ "$command" == *"$root/bin/llvmnet-clang++"* ]] || { printf 'Cannot find the link command of %s in %s\n' "$target" "$build_dir" >&2; return 1; }
    rm -rf "${build_dir:?}/$target" "${build_dir:?}/$target.llvmnet"
    (cd "$build_dir" && env ${link_env[@]+"${link_env[@]}"} bash -c "$command")
}
