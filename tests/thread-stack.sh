#!/usr/bin/env bash
# tags: core aot
# Thread stack sizes (FT-08 follow-up): pthread_attr_setstacksize takes effect on managed threads, a default thread
# gets at least 8 MB, and getrlimit(RLIMIT_STACK) reports the process's limit. tests/c/thread-stack.c must print
# what the native build prints under a 16 MB stack limit, in both modes under JIT and NativeAOT.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
source_file="$root/tests/c/thread-stack.c"
clang-22 -O1 -pthread "$source_file" -o "$test_output/native"
(ulimit -S -s 16384 && "$test_output/native" >"$test_output/native.txt")
grep -q '^stack limit 16384 KB' "$test_output/native.txt"
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" -O1 "$source_file" -o "$test_output/$runtime-stack.dll"
    (ulimit -S -s 16384 && dotnet "$test_output/$runtime-stack.dll" >"$test_output/$runtime.txt")
    diff -u "$test_output/native.txt" "$test_output/$runtime.txt"
done
"$llvmnet" --runtime=portable -O1 --nativeaot "$source_file" -o "$test_output/portable-stack-aot" >"$test_output/aot.log"
(ulimit -S -s 16384 && "$test_output/portable-stack-aot" >"$test_output/portable-aot.txt")
diff -u "$test_output/native.txt" "$test_output/portable-aot.txt"
printf 'PASS: thread stack sizes and the stack limit match native in both modes (JIT and NativeAOT)\n'
