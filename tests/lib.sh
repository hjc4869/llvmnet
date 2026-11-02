# shellcheck shell=bash disable=SC2034
# Shared prologue for tests/*.sh. Source it as the first command:
#   source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
# It sets strict mode, the repository root, common tool paths, and builds the solution once
# unless the test runner (tests/run.sh) already did.
set -euo pipefail
ulimit -c 0 2>/dev/null || true  # tests that crash on purpose must not leave core files
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
llvmnet="$root/bin/llvmnet"
compiler_dll="$root/src/LlvmNet.Compiler/bin/Release/net10.0/llvmnet.dll"
checks="$root/tests/LlvmNet.Checks/bin/Release/net10.0/LlvmNet.Checks.dll"
test_name="$(basename "${BASH_SOURCE[1]:-${0}}" .sh)"
test_output="$root/artifacts/tests/$test_name"
if [[ "${LLVMNET_SKIP_BUILD:-0}" != 1 ]]; then
    bash "$root/scripts/ensure-built.sh"
    export LLVMNET_SKIP_BUILD=1
fi

# Compiles LLVM IR for one runtime mode without optimizing it. Link inputs must carry the ABI tag llvmnet -c
# records, so IR fixtures go through this before they are linked: tag_ir <runtime> <input.ll> <output.bc>
tag_ir() { "$llvmnet" --runtime="$1" -x ir -O0 -c "$2" -o "$3" 2>"$3.log" || { cat "$3.log" >&2; return 1; }; }

# A toolchain root for tests that install native extensions: links to everything in the real root, except that the
# portable sysroot is a directory of links with an empty ext/ of its own. Run llvmnet with LLVMNET_ROOT=<dir>.
#   extension_root <dir>
extension_root() {
    local farm="$1" entry
    rm -rf "$farm"
    mkdir -p "$farm/sysroot/dotnet64-v2/ext"
    for entry in "$root"/*; do [[ "${entry##*/}" == sysroot ]] || ln -s "$entry" "$farm/"; done
    for entry in "$root"/sysroot/*; do [[ "${entry##*/}" == dotnet64-v2 ]] || ln -s "$entry" "$farm/sysroot/"; done
    for entry in "$root"/sysroot/dotnet64-v2/* "$root"/sysroot/dotnet64-v2/.llvmnet-*; do
        [[ "${entry##*/}" == ext ]] || ln -s "$entry" "$farm/sysroot/dotnet64-v2/"
    done
}
