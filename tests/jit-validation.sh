#!/usr/bin/env bash
# tags: core
# serial: 1
# timeout: 1800
# JIT-compile every method of every llvmnet assembly emitted by the tests that ran before it in this run,
# plus the runtime library, so invalid IL fails here instead of on an untested execution path.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
"$llvmnet" --runtime=portable "$root/tests/c/core.c" -o "$test_output/core.dll"
# Under the runner, only assemblies built in this run: earlier runs may have left outputs of removed tests.
marker="$root/artifacts/tests/results/started"
if [[ "${LLVMNET_TEST_RUNNER:-0}" == 1 && -f "$marker" ]]; then
    mapfile -t assemblies < <(find "$root/artifacts/tests" -name '*.dll' -newer "$marker" -not -path '*/results/*')
    dotnet "$checks" jit-all "${assemblies[@]}"
else
    dotnet "$checks" jit-all "$root/artifacts/tests"
fi
