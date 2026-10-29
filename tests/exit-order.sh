#!/usr/bin/env bash
# tags: core cpp aot
# requires: clang++
# Termination order (BE-08) against native glibc: thread_local destructors, then atexit and __cxa_atexit
# handlers interleaved in reverse registration order (including ones registered while exiting), then
# destructor attributes by descending priority, for both return from main and exit().
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
fixture="$root/tests/cpp/exit-order.cpp"
rm -rf "$test_output"
mkdir -p "$test_output"
clang++-22 -O1 "$fixture" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
"$test_output/native" exit >"$test_output/native-exit.txt"
cmp "$test_output/native.txt" "$test_output/native-exit.txt"
for runtime in managed-host system portable; do
    "$llvmnet" --runtime="$runtime" -O1 "$fixture" -o "$test_output/exit-order-$runtime.dll"
    for mode in return exit; do
        dotnet "$test_output/exit-order-$runtime.dll" "$mode" >"$test_output/$runtime-$mode.txt"
        diff -u "$test_output/native.txt" "$test_output/$runtime-$mode.txt"
    done
    if [[ "$runtime" != managed-host ]]; then
        "$llvmnet" --runtime="$runtime" -O1 --nativeaot "$fixture" -o "$test_output/exit-order-$runtime-aot"
        for mode in return exit; do
            "$test_output/exit-order-$runtime-aot" "$mode" >"$test_output/$runtime-aot-$mode.txt"
            diff -u "$test_output/native.txt" "$test_output/$runtime-aot-$mode.txt"
        done
    fi
done
printf 'PASS: termination order matches native for return and exit() in all modes, JIT and NativeAOT\n'
