#!/usr/bin/env bash
# tags: core aot
# musttail calls run in constant stack: tests/c/musttail.c chains 3,000,000 of them (direct, through function pointers,
# and from frames with an alloca, a variadic call or a setjmp) and must print what the native build prints, in both
# runtime modes, under the JIT and NativeAOT, which has no tail-call helpers and so needs every call to be a fast tail
# call. tests/ir/musttail-values.ll chains 16-byte values (vectors, i128, long double), which llvmnet passes as value
# types; the musttail calls no tail call can carry are rejected with a diagnostic.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
ulimit -S -s 8192
source_file="$root/tests/c/musttail.c"
clang-22 -O2 "$source_file" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
for runtime in system portable; do
    for level in -O0 -O2; do
        target="$test_output/$runtime$level"
        "$llvmnet" --runtime="$runtime" "$level" "$source_file" -o "$target.dll"
        timeout 120 dotnet "$target.dll" >"$target.txt"
        diff -u "$test_output/native.txt" "$target.txt"
    done
    # The calls stay musttail calls at -O2 (no inlining or loop turns them into jumps), and become tail. calls; in
    # system mode an indirect call has a second, ordinary calli for a pointer to a native function.
    for function in even stage with_alloca with_varargs with_setjmp; do
        dotnet "$checks" il-opcodes "$test_output/$runtime-O2.dll" "$function" | grep -Eq ' tail\. (call|calli) ret( calli ret)?$'
    done
    levels=(-O2)
    # At -O0 every function keeps its allocas, so the indirect chain also takes its frame from the arena.
    [[ "$runtime" == portable ]] && levels+=(-O0)
    for level in "${levels[@]}"; do
        target="$test_output/$runtime$level-aot"
        "$llvmnet" --runtime="$runtime" "$level" --nativeaot "$source_file" -o "$target" >"$target.log"
        timeout 120 "$target" >"$target.txt"
        diff -u "$test_output/native.txt" "$target.txt"
    done
    # ir.sh runs the 16-byte fixture under the JIT; NativeAOT here.
    tag_ir "$runtime" "$root/tests/ir/musttail-values.ll" "$test_output/values-$runtime.bc"
    "$llvmnet" --runtime="$runtime" --nativeaot "$test_output/values-$runtime.bc" -o "$test_output/values-$runtime-aot" >"$test_output/values-$runtime-aot.log"
    timeout 120 "$test_output/values-$runtime-aot"
    printf 'PASS: %s musttail chains run in constant stack under the JIT and NativeAOT\n' "$runtime"
done
declare -A diagnostics=([byval]='a byval argument cannot be a tail call' [varargs]='forward variadic arguments cannot be tail calls'
    [aggregate]='arguments and results of more than 16 bytes cannot be tail calls')
for fixture in "${!diagnostics[@]}"; do
    tag_ir portable "$root/tests/ir/rejected/musttail-$fixture.ll" "$test_output/rejected-$fixture.bc"
    if "$llvmnet" "$test_output/rejected-$fixture.bc" -o "$test_output/rejected-$fixture.dll" >"$test_output/rejected-$fixture.log" 2>&1; then
        printf 'FAIL: a musttail call with a %s argument was accepted\n' "$fixture" >&2
        exit 1
    fi
    grep -q "error: musttail call to .*${diagnostics[$fixture]}" "$test_output/rejected-$fixture.log"
done
printf 'PASS: musttail calls with byval, forwarded variadic or 32-byte arguments are rejected with a diagnostic\n'
