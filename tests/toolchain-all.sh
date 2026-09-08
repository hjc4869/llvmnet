#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
bash "$root/tests/all.sh" core
bash "$root/tests/driver.sh"
bash "$root/tests/system-abi.sh"
bash "$root/tests/portable.sh"
bash "$root/tests/portable-cpp.sh"
bash "$root/tests/portable-spec.sh"
bash "$root/tests/modes.sh"
bash "$root/tests/toolchain.sh"
bash "$root/scripts/package-toolchain.sh" "$root/artifacts/deploy-toolchain" --self-contained --bundle-llvm
bash "$root/tests/deployment.sh" "$root/artifacts/deploy-toolchain"
printf 'PASS: deployed dual-ABI C/C++/Fortran toolchain and NativeAOT checks\n'