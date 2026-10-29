#!/usr/bin/env bash
# tags: core
# serial: 1
# timeout: 1800
# JIT-compile every method of every llvmnet assembly emitted by the tests that ran before it, plus
# the runtime library, so invalid IL fails here instead of on an untested execution path.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
"$llvmnet" --runtime=portable "$root/tests/c/core.c" -o "$test_output/core.dll"
dotnet "$checks" jit-all "$root/artifacts/tests"
