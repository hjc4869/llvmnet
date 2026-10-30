#!/usr/bin/env bash
# Build the solution and the portable sysroot once. No-op when LLVMNET_SKIP_BUILD=1, which tests/run.sh and
# tests/lib.sh set after building so nested scripts do not rebuild concurrently.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${LLVMNET_SKIP_BUILD:-0}" == 1 ]]; then exit 0; fi
dotnet build "$root/llvmnet.slnx" -c Release --nologo -v quiet -clp:ErrorsOnly
LLVMNET_SKIP_BUILD=1 bash "$root/scripts/build-sysroot.sh" >/dev/null
