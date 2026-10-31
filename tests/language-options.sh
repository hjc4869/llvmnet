#!/usr/bin/env bash
# tags: core cpp fortran
# requires: flang
# TC-04: -g locates compiler errors, -fno-exceptions and -fno-rtti build C++ without landing pads or type_info,
# -fvisibility=hidden controls what a managed library exports, the language-standard defaults follow the frontend per
# driver alias, and -m flags are rejected in portable mode with a message.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
# -g: the diagnostic for an unsupported construct names the file, line and column.
if "$llvmnet" -g "$root/tests/c/computed-goto.c" -o computed-goto.dll >computed-goto.log 2>&1; then
    printf 'FAIL: computed goto was accepted\n' >&2
    exit 1
fi
grep -q -E '^.*tests/c/computed-goto\.c:[0-9]+(:[0-9]+)?: error: ' computed-goto.log
grep -q "note: in function 'main'" computed-goto.log
if "$llvmnet" "$root/tests/c/computed-goto.c" -o computed-goto.dll >computed-goto-nog.log 2>&1; then exit 1; fi
grep -q 'compile with -g or -gline-tables-only for source locations' computed-goto-nog.log
printf 'PASS: -g gives compiler errors a file:line:col location\n'
# Language-standard defaults per alias match the frontend's own, in both modes.
clang-22 "$root/tests/c/dialect.c" -o dialect-native-c && ./dialect-native-c >dialect-native-c.txt
clang++-22 -x c++ "$root/tests/c/dialect.c" -o dialect-native-cpp && ./dialect-native-cpp >dialect-native-cpp.txt
for runtime in system portable; do
    LLVMNET_DRIVER=clang "$llvmnet" --runtime="$runtime" "$root/tests/c/dialect.c" -o dialect-c-$runtime.dll
    dotnet dialect-c-$runtime.dll | diff - dialect-native-c.txt
    # llvmnet-clang++ compiles a .c file as C++, as clang++ does.
    LLVMNET_DRIVER=clang++ "$llvmnet" --runtime="$runtime" "$root/tests/c/dialect.c" -o dialect-cpp-$runtime.dll
    dotnet dialect-cpp-$runtime.dll | diff - dialect-native-cpp.txt
    "$llvmnet" --runtime="$runtime" --driver-mode=g++ "$root/tests/c/dialect.c" -o dialect-mode-$runtime.dll
    dotnet dialect-mode-$runtime.dll | diff - dialect-native-cpp.txt
    "$llvmnet" --runtime="$runtime" -std=c11 "$root/tests/c/dialect.c" -o dialect-c11-$runtime.dll
    dotnet dialect-c11-$runtime.dll | grep -q '^c 201112$'
    LLVMNET_DRIVER=clang++ "$llvmnet" --runtime="$runtime" -std=c++20 "$root/tests/c/dialect.c" -o dialect-cpp20-$runtime.dll
    dotnet dialect-cpp20-$runtime.dll | grep -q '^c++ 202002$'
done
printf 'PASS: C and C++ defaults (%s, %s) follow the frontend per alias; -std= overrides\n' "$(cat dialect-native-c.txt)" "$(cat dialect-native-cpp.txt)"
# -fno-exceptions -fno-rtti: no invoke or landingpad in the IR, no type_info, same output as native.
clang++-22 -fno-exceptions -fno-rtti "$root/tests/cpp/no-eh.cpp" -o no-eh-native && ./no-eh-native >no-eh-native.txt
grep -q 'exceptions disabled' no-eh-native.txt
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" -fno-exceptions -fno-rtti --keep-ir no-eh-$runtime.bc "$root/tests/cpp/no-eh.cpp" -o no-eh-$runtime.dll
    dotnet no-eh-$runtime.dll | diff - no-eh-native.txt
    llvm-dis-22 no-eh-$runtime.bc -o no-eh-$runtime.ll
    # The program's own functions have no exception handling and no type_info; the runtime libraries keep theirs.
    if grep -E '^define .*(Square|Oblong|sum)' -A 40 no-eh-$runtime.ll | grep -q -E 'landingpad|invoke '; then
        printf 'FAIL: -fno-exceptions left landing pads in the program (%s)\n' "$runtime" >&2
        exit 1
    fi
    if grep -q '_ZTI6Square' no-eh-$runtime.ll; then
        printf 'FAIL: -fno-rtti left type_info for the program classes (%s)\n' "$runtime" >&2
        exit 1
    fi
    "$llvmnet" --runtime="$runtime" --nativeaot -fno-exceptions -fno-rtti "$root/tests/cpp/no-eh.cpp" -o no-eh-$runtime-aot >/dev/null
    ./no-eh-$runtime-aot | diff - no-eh-native.txt
done
printf 'PASS: -fno-exceptions and -fno-rtti build C++ without landing pads or type_info in both modes, JIT and NativeAOT\n'
# -fvisibility=hidden: a managed library exports only default-visibility external definitions.
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" -shared -fvisibility=hidden "$root/tests/c/visibility.c" -o Visibility-$runtime.dll
    dotnet "$checks" inspect Visibility-$runtime.dll >inspect-$runtime.txt
    grep -q '^exports: exported_function exported_user$' inspect-$runtime.txt
    "$llvmnet" --runtime="$runtime" "$root/tests/c/visibility-client.c" --cil-reference Visibility-$runtime.dll -o client-$runtime.dll
    dotnet client-$runtime.dll
    if "$llvmnet" --runtime="$runtime" -DUSE_HIDDEN "$root/tests/c/visibility-client.c" --cil-reference Visibility-$runtime.dll -o hidden-client-$runtime.dll >hidden-$runtime.log 2>&1; then
        printf 'FAIL: a hidden function was resolved through --cil-reference (%s)\n' "$runtime" >&2
        exit 1
    fi
    grep -q -E 'Unresolved (function|system ABI symbol):? hidden_function' hidden-$runtime.log
    # Without -fvisibility=hidden every external definition is exported; static functions never are.
    "$llvmnet" --runtime="$runtime" -shared "$root/tests/c/visibility.c" -o VisibleAll-$runtime.dll
    dotnet "$checks" inspect VisibleAll-$runtime.dll | grep -q '^exports: exported_function exported_user hidden_function uses_both$'
done
printf 'PASS: -fvisibility=hidden and visibility attributes select the exports of a managed library\n'
# -m flags are rejected in portable mode with a message, and passed to the frontend in system mode.
if "$llvmnet" --runtime=portable -msse4.2 -c "$root/tests/c/smoke.c" -o m.o >m.log 2>&1; then
    printf 'FAIL: -msse4.2 was accepted in portable mode\n' >&2
    exit 1
fi
grep -q "option '-msse4.2' is not accepted in portable mode" m.log
"$llvmnet" --runtime=system -msse4.2 -c "$root/tests/c/smoke.c" -o m.o
printf 'PASS: -m flags are rejected in portable mode with a message and accepted in system mode\n'
