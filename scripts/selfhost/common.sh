# shellcheck shell=bash disable=SC2034
# Settings shared by the scripts that build LLVM, Clang and Flang 22 with llvmnet (scripts/selfhost/README.md).
#   LLVMNET_SELFHOST_DIR        work directory: the patched source copy and the build tree (default artifacts/selfhost-build)
#   LLVMNET_SELFHOST_TOOLCHAIN  the llvmnet toolchain that compiles LLVM (default this checkout: bin/, cmake/)
#   NATIVE_LLVM                 directory of the host's LLVM 22 tools for TableGen (default llvm-config-22 --bindir)
#   JOBS                        parallel compile jobs (default nproc)
selfhost="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$selfhost/../.." && pwd)"
work="$(realpath -m "${LLVMNET_SELFHOST_DIR:-$root/artifacts/selfhost-build}")"
source_dir="$work/llvm-project"
build_dir="$work/llvm"
toolchain="$(realpath -m "${LLVMNET_SELFHOST_TOOLCHAIN:-$root}")"
jobs="${JOBS:-$(nproc)}"
native_llvm="${NATIVE_LLVM:-$(llvm-config-22 --bindir 2>/dev/null || true)}"
toolchain_file="$toolchain/cmake/llvmnet.cmake"
[[ -f "$toolchain_file" ]] || toolchain_file="$toolchain/share/llvmnet/cmake/llvmnet.cmake"

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
