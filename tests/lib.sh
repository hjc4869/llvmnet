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
