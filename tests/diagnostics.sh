#!/usr/bin/env bash
# tags: core
# TC-05: Clang's and Flang's diagnostics pass through unchanged (text, colors, -fdiagnostics-format), llvmnet's own
# errors have the same form and colors, exit codes are 1 (compile), 2 (usage) and the frontend's own, and a stack trace
# appears only with LLVMNET_TRACE=1.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
status() { local code=0; "$@" >/dev/null || code=$?; echo "$code"; }
source_file="$root/tests/c/undeclared.c"
for runtime in system portable; do
    for flags in "" "-fcolor-diagnostics" "-fno-color-diagnostics" "-fdiagnostics-format=msvc" "-fdiagnostics-format=vi -fno-show-column" "-ferror-limit=1 -Wall -Wextra"; do
        # shellcheck disable=SC2206
        flag_list=($flags)
        native=$(status clang-22 "${flag_list[@]}" -c "$source_file" -o native.o 2>native.err)
        managed=$(status "$llvmnet" --runtime="$runtime" "${flag_list[@]}" -c "$source_file" -o managed.o 2>managed.err)
        if [[ "$native" != "$managed" ]]; then
            printf 'FAIL: exit %s from llvmnet, %s from clang for "%s" (%s)\n' "$managed" "$native" "$flags" "$runtime" >&2
            exit 1
        fi
        # The frontend's stderr is passed through byte for byte: no llvmnet line is added.
        diff native.err managed.err
    done
    grep -q $'\e\\[0;1;31merror: ' <("$llvmnet" --runtime="$runtime" -fcolor-diagnostics -c "$source_file" -o x.o 2>&1 || true)
    printf 'PASS: %s: Clang diagnostics, colors, formats and exit status pass through unchanged\n' "$runtime"
done
flang-22 -c "$root/tests/fortran/core.f90" -o /dev/null 2>/dev/null
printf 'program broken\n  integer :: x\n  x = \n end program\n' >broken.f90
native=$(status flang-22 -c broken.f90 -o native.o 2>native.err)
managed=$(status "$llvmnet" --runtime=portable -c broken.f90 -o managed.o 2>managed.err)
test "$native" = "$managed" && diff native.err managed.err
printf 'PASS: Flang diagnostics and exit status pass through unchanged\n'
# llvmnet's own errors: Clang's form, Clang's colors on request, no stack trace without LLVMNET_TRACE.
code=$(status "$llvmnet" -g "$root/tests/c/computed-goto.c" -o goto.dll 2>goto.err)
test "$code" -eq 1
grep -q -E '^[^ ]*computed-goto\.c:[0-9]+(:[0-9]+)?: error: ' goto.err
grep -q -E "^llvmnet: note: in function 'main'" goto.err
if grep -q '^   at ' goto.err; then printf 'FAIL: a stack trace without LLVMNET_TRACE\n' >&2; exit 1; fi
code=$(status env LLVMNET_TRACE=1 "$llvmnet" -g "$root/tests/c/computed-goto.c" -o goto.dll 2>goto-trace.err)
test "$code" -eq 1
grep -q '^   at LlvmNet' goto-trace.err
code=$(status "$llvmnet" -fcolor-diagnostics -g "$root/tests/c/computed-goto.c" -o goto.dll 2>goto-color.err)
test "$code" -eq 1
grep -q $'\e\\[0;1;31merror: ' goto-color.err
grep -q $'\e\\[0;1;30mnote: ' goto-color.err
code=$(status "$llvmnet" -fno-color-diagnostics -g "$root/tests/c/computed-goto.c" -o goto.dll 2>goto-plain.err)
if grep -q $'\e' goto-plain.err; then printf 'FAIL: colors with -fno-color-diagnostics\n' >&2; exit 1; fi
# Usage errors: exit 2, one line, the option named.
code=$(status "$llvmnet" -rdynamic "$root/tests/c/smoke.c" -o x.dll 2>usage.err)
test "$code" -eq 2
test "$(wc -l <usage.err)" -eq 1
grep -q "^llvmnet: error: option '-rdynamic' is not accepted" usage.err
code=$(status "$llvmnet" 2>/dev/null)
test "$code" -eq 2
# A link error: exit 1 with the unresolved symbol.
printf 'extern int missing_symbol(void);\nint main(void) { return missing_symbol(); }\n' >unresolved.c
code=$(status "$llvmnet" unresolved.c -o unresolved.dll 2>unresolved.err)
test "$code" -eq 1
grep -q 'Unresolved function: missing_symbol' unresolved.err
printf 'PASS: llvmnet errors use Clang'"'"'s form and colors, exit 1 or 2, and trace only with LLVMNET_TRACE=1\n'
