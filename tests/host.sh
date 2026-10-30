#!/usr/bin/env bash
# tags: core libc aot
# Host interface conformance: tests/c/host-errors.c checks the results and errno values of the system services
# the portable host layer implements. Each build runs in its own empty directory and must print exactly what the
# native build prints, under JIT and NativeAOT.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output/native" "$test_output/jit" "$test_output/aot"
source_file="$root/tests/c/host-errors.c"
clang-22 -O1 -w "$source_file" -o "$test_output/host-native"
(cd "$test_output/native" && ../host-native >../native.txt)
"$llvmnet" --runtime=portable -O1 -w "$source_file" -o "$test_output/host.dll"
(cd "$test_output/jit" && timeout 120 dotnet ../host.dll >../jit.txt)
diff -u "$test_output/native.txt" "$test_output/jit.txt"
"$llvmnet" --runtime=portable -O1 -w --nativeaot "$source_file" -o "$test_output/host-aot" >"$test_output/aot.log"
(cd "$test_output/aot" && timeout 120 ../host-aot >../aot.txt)
diff -u "$test_output/native.txt" "$test_output/aot.txt"
printf 'PASS: %d host interface checks match native under JIT and NativeAOT\n' "$(wc -l <"$test_output/native.txt")"
# SIGTERM from another process reaches the installed handler.
"$llvmnet" --runtime=portable -O1 "$root/tests/c/host-signal.c" -o "$test_output/signal.dll"
deliver() {
    local output="$1"
    shift
    "$@" >"$output" &
    local pid=$!
    for _ in $(seq 200); do grep -q ready "$output" && break; sleep 0.05; done
    kill -TERM "$pid"
    wait "$pid"
    grep -q 'received signal 15' "$output"
}
deliver "$test_output/signal-jit.txt" dotnet "$test_output/signal.dll"
printf 'PASS: SIGTERM from another process runs the portable handler\n'
