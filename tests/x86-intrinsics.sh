#!/usr/bin/env bash
# tags: core aot
# x86 target intrinsics (llvm.x86.*). System mode predefines __SSE__ and __SSE2__ as native x86-64 does, so libraries
# take their SSE paths; the backend translates only the spin hint and the fences. Every other x86 intrinsic gets a
# warning when its unit is compiled (-w silences it) and is rejected when a program that reaches it is linked; the link
# drops unreferenced functions, so a unit that merely contains one still links. -U__SSE__ -U__SSE2__ selects such a
# library's C path.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
status() { local code=0; "$@" >/dev/null || code=$?; echo "$code"; }
sse="$root/tests/c/x86-sse-path.c"
clang-22 -O2 "$sse" -o sse-native
./sse-native >sse-native.txt
"$llvmnet" --runtime=system -O2 -c "$sse" -o sse.o 2>sse.err
grep -q '^llvmnet: warning: x86 target intrinsic llvm.x86.sse2.psad.bw is not translated, so linking a program that reaches it fails; build the code.s portable C path instead (system mode predefines __SSE__ and __SSE2__' sse.err
grep -q "^llvmnet: note: in function '\(sad16\|main\)' (compile with -g" sse.err
"$llvmnet" --runtime=system -O2 -g -c "$sse" -o sse-g.o 2>sse-g.err
grep -q -E '^[^ ]*x86-sse-path\.c:[0-9]+:[0-9]+: warning: x86 target intrinsic llvm\.x86\.sse2\.psad\.bw' sse-g.err
"$llvmnet" --runtime=system -O2 -w -c "$sse" -o sse-w.o 2>sse-w.err
test ! -s sse-w.err
code=$(status "$llvmnet" --runtime=system sse.o -o sse.dll 2>sse-link.err)
test "$code" -eq 1
grep -q '^llvmnet: error: x86 target intrinsic llvm.x86.sse2.psad.bw is not translated; build the code.s portable C path instead (system mode' sse-link.err
"$llvmnet" --runtime=system -O2 -U__SSE__ -U__SSE2__ "$sse" -o sse-c.dll 2>sse-c.err
test ! -s sse-c.err
dotnet sse-c.dll >sse-c.txt
diff -u sse-native.txt sse-c.txt
# A unit whose x86 code no program reaches warns and still links.
printf '#include <xmmintrin.h>\nunsigned csr(void) { return _mm_getcsr(); }\nint main(void) { return 0; }\n' >unreached.c
"$llvmnet" --runtime=system -O1 unreached.c -o unreached.dll 2>unreached.err
grep -q 'warning: x86 target intrinsic llvm.x86.sse.stmxcsr is not translated' unreached.err
dotnet unreached.dll
printf 'PASS: system-mode SSE intrinsics warned about while compiling (with -g locations) and rejected at the link; -U__SSE2__ selects the C path\n'
hints="$root/tests/c/x86-hints.c"
clang-22 -O1 "$hints" -o hints-native
./hints-native >hints-native.txt
for runtime in system portable; do
    "$llvmnet" --runtime="$runtime" -O1 "$hints" -o "hints-$runtime.dll"
    dotnet "hints-$runtime.dll" >"hints-$runtime.txt"
    diff -u hints-native.txt "hints-$runtime.txt"
done
"$llvmnet" --runtime=system --nativeaot -O1 "$hints" -o hints-aot >hints-aot.log
./hints-aot >hints-aot.txt
diff -u hints-native.txt hints-aot.txt
printf 'PASS: the x86 spin hint and fences translate in both modes, under JIT and NativeAOT\n'
