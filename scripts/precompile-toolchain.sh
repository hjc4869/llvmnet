#!/usr/bin/env bash
# Precompile a portable toolchain in place for one runtime identifier (XP-04): the compiler, LlvmNet.Llvm.dll, the runtime
# and every tool payload, with scripts/readytorun.sh. The toolchain still runs on other hosts, under the JIT; on hosts of
# the identifier a compile starts in about a second instead of five to ten.
# Usage: scripts/precompile-toolchain.sh <portable-toolchain-prefix> <rid>   (JOBS as for readytorun.sh)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="${1:?usage: precompile-toolchain.sh <prefix> <rid>}"
rid="${2:?usage: precompile-toolchain.sh <prefix> <rid>}"
[[ -f "$prefix/lib/llvmnet/LlvmNet.Llvm.dll" ]] || { printf '%s is not a portable toolchain (no lib/llvmnet/LlvmNet.Llvm.dll).\n' "$prefix" >&2; exit 2; }
bash "$root/scripts/readytorun.sh" "$rid" "$prefix"/lib/llvmnet/llvmnet.dll "$prefix"/lib/llvmnet/LlvmNet.Llvm.dll \
    "$prefix"/lib/llvmnet/LlvmNet.Runtime.dll "$prefix"/libexec/llvm/bin/*.llvmnet/program.dll "$prefix"/libexec/llvm/bin/*.llvmnet/LlvmNet.Runtime.dll >/dev/null
printf '%s\n' "$rid" >"$prefix/lib/llvmnet/readytorun.txt"
