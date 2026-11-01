#!/usr/bin/env bash
# tags: core aot
# RL-02: dotnet64-v2 is frozen (ADR-09); docs/user/abi.md is the contract. The test fails when any part changes:
# - the header set: the stamp build-sysroot.sh writes over every installed header must equal tests/abi/dotnet64-v2.headers;
# - the data layout: tests/abi/layout.c, built in portable mode, prints the sizes, alignments, offsets, limits, signal
#   and errno numbers of the contract, which must equal tests/abi/dotnet64-v2.layout;
# - objects from an earlier build: tests/abi/compat-object-v2.o, LLVM bitcode compiled when the ABI was frozen, must
#   link with a freshly compiled tests/abi/compat-main.c and print tests/abi/compat.expected (structures by value,
#   varargs, long double, errno, jmp_buf across the boundary, callbacks, stdio), under the JIT and as NativeAOT.
# A deliberate, compatible change updates the expected file in the same commit and says why; an incompatible one
# needs a new ABI tag.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
abi="$root/tests/abi"
if [[ "$(<"$root/sysroot/dotnet64-v2/.llvmnet-headers")" != "$(<"$abi/dotnet64-v2.headers")" ]]; then
    printf 'FAIL: the dotnet64-v2 header set changed (sysroot stamp %s, frozen %s)\n' \
        "$(<"$root/sysroot/dotnet64-v2/.llvmnet-headers")" "$(<"$abi/dotnet64-v2.headers")" >&2
    exit 1
fi
"$llvmnet" --runtime=portable -w "$abi/layout.c" -o "$test_output/layout"
"$test_output/layout" >"$test_output/layout.txt"
diff -u "$abi/dotnet64-v2.layout" "$test_output/layout.txt" ||
    { printf 'FAIL: the dotnet64-v2 data layout changed\n' >&2; exit 1; }
"$llvmnet" --runtime=portable -O1 "$abi/compat-main.c" "$abi/compat-object-v2.o" -o "$test_output/compat"
"$test_output/compat" | diff -u "$abi/compat.expected" - ||
    { printf 'FAIL: a program linked with the frozen dotnet64-v2 object behaves differently\n' >&2; exit 1; }
"$llvmnet" --runtime=portable -O1 --nativeaot "$abi/compat-main.c" "$abi/compat-object-v2.o" -o "$test_output/compat-aot" \
    >"$test_output/aot.log" 2>&1 || { cat "$test_output/aot.log" >&2; exit 1; }
"$test_output/compat-aot" | diff -u "$abi/compat.expected" - ||
    { printf 'FAIL: the NativeAOT program linked with the frozen dotnet64-v2 object behaves differently\n' >&2; exit 1; }
printf 'PASS: the dotnet64-v2 header set, data layout and an object from the freeze are unchanged (JIT and NativeAOT)\n'
