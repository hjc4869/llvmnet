#!/usr/bin/env bash
set -euo pipefail
ulimit -c 0
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/cpp-terminate"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang++-22 -std=c++17 -O1 "$root/tests/cpp/terminate.cpp" -o "$output/native"
check_exit() {
    local expected="$1" log="$2" code=0
    shift 2
    timeout 30 "$@" > "$log" 2>&1 || code=$?
    if [[ "$code" != "$expected" ]]; then
        printf 'Expected exit %s, got %s: %s\n' "$expected" "$code" "$log" >&2
        return 1
    fi
}
for scenario in invoke noexcept default return throw; do
    expected=134
    if [[ "$scenario" == invoke || "$scenario" == noexcept ]]; then expected=73; fi
    check_exit "$expected" "$output/native-$scenario.log" "$output/native" "$scenario"
done
for runtime in system portable; do
    target="$output/terminate-$runtime"
    "$root/bin/llvmnet" --runtime="$runtime" -std=c++17 -O1 "$root/tests/cpp/terminate.cpp" -o "$target.dll"
    "$root/bin/llvmnet" --runtime="$runtime" --nativeaot -std=c++17 -O1 "$root/tests/cpp/terminate.cpp" -o "$target-aot"
    for scenario in invoke noexcept default return throw; do
        expected=134
        if [[ "$scenario" == invoke || "$scenario" == noexcept ]]; then expected=73; fi
        check_exit "$expected" "$target-$scenario.log" dotnet "$target.dll" "$scenario"
        check_exit "$expected" "$target-aot-$scenario.log" "$target-aot" "$scenario"
    done
    printf 'PASS: %s C++ terminate handlers, noexcept and non-returning termination in JIT/NativeAOT\n' "$runtime"
done