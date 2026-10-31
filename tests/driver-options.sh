#!/usr/bin/env bash
# tags: core toolchain fortran
# requires: python3 flang
# timeout: 1800
# TC-01: every option Clang's and Flang's own --help list, in both runtime modes, either works or produces a clear
# error (the frontend's own, or llvmnet's reason); the generated frontend option lists must match the frontends.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
python3 "$root/scripts/frontend-options.py" --check
python3 "$root/tests/driver/options.py" "$llvmnet" "$test_output/work" "$test_output/options.tsv" --jobs "${LLVMNET_OPTION_JOBS:-8}"
printf 'PASS: every frontend option works or fails with a clear error in both modes; %s\n' "$test_output/options.tsv"
