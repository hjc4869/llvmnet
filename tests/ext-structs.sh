#!/usr/bin/env bash
# tags: core aot
# requires: python3
# NX-07: structures passed by value and returned. tests/extensions/structs/ describes, by its C header, an API whose
# entry points take and return a pair of floats, an int with a double, 17 bytes, four doubles, a nested structure and a
# 3-byte one. The generated extension links C wrappers into the program (lib/wrappers.o) that call internal stubs with
# pointers, and the stubs pass typed CLR structures to the host, so that the CLR classifies them by the host's
# convention. The program calls every entry point directly and two through dlsym, and must print
# tests/extensions/structs.expected natively, under the JIT, as NativeAOT and in system mode. The tests/xp/ bundle runs
# the same program on Windows, macOS and Linux ARM64 against a library built there.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output/native"
fixtures="$root/tests/extensions"
expected="$fixtures/structs.expected"
include="$fixtures/structs/include"
native="$test_output/native"
clang-22 -shared -fPIC -O1 -I"$include" "$fixtures/structs/native.c" -o "$native/libllvmnet-structs.so"
clang-22 -O1 -I"$include" "$fixtures/structs.c" -L"$native" -l:libllvmnet-structs.so -o "$test_output/structs-native"
LD_LIBRARY_PATH="$native" "$test_output/structs-native" | diff -u "$expected" -
toolchain="$test_output/toolchain"
extension_root "$toolchain"
bash "$root/scripts/build-extensions.sh" --sysroot "$toolchain/sysroot/dotnet64-v2" --build-dir "$test_output/ext-build" \
    "$fixtures/structs/extension.json" 2>"$test_output/build.log" || { cat "$test_output/build.log" >&2; exit 1; }
grep -q 'verified 11 stubs against the C prototypes llvmnet compiled' "$test_output/build.log" || { cat "$test_output/build.log" >&2; exit 1; }
test -f "$toolchain/sysroot/dotnet64-v2/ext/structs/lib/wrappers.o"
export LLVMNET_ROOT="$toolchain"
"$llvmnet" -O1 "$fixtures/structs.c" -lllvmnet-structs -o "$test_output/jit/structs.dll"
LLVMNET_EXT_STRUCTS_DIR="$native" dotnet "$test_output/jit/structs.dll" | diff -u "$expected" -
mkdir -p "$test_output/aot"
"$llvmnet" -O1 --nativeaot "$fixtures/structs.c" -lllvmnet-structs -o "$test_output/aot/structs" >"$test_output/aot.log" 2>&1 ||
    { cat "$test_output/aot.log" >&2; exit 1; }
LLVMNET_EXT_STRUCTS_DIR="$native" "$test_output/aot/structs" | diff -u "$expected" -
# System mode calls the library directly; it does not call native function pointers with structures by value.
LIBRARY_PATH="$native" LD_LIBRARY_PATH="$native" "$llvmnet" --runtime=system -O1 -DSTRUCTS_NO_DLSYM -I"$include" "$fixtures/structs.c" \
    --extension=structs -o "$test_output/system/structs.dll"
LD_LIBRARY_PATH="$native" dotnet "$test_output/system/structs.dll" | diff -u <(grep -v '^through dlsym' "$expected") -
printf 'PASS: structures by value reach the host and come back as natively (JIT, NativeAOT, system mode)\n'
