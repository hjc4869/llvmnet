#!/usr/bin/env bash
# tags: core aot
# x86 target intrinsics (llvm.x86.*, ADR-16 Level 2, SV-08, SV-09). The runtime implements the SSE to SSE4.2 intrinsics,
# PCLMULQDQ, emms, the spin hint and the fences, and those of x86-64-v3, AVX, AVX2, FMA, F16C and BMI (LlvmNet.Runtime.X86):
# each runs the host's instruction behind an IsSupported branch or a composition with the instruction's results (for
# 256 bits, the 128-bit helpers on each half). --isa=x86-64-v2 and x86-64-v3 give portable code the x86 macros of their
# level, as -march does natively; system mode predefines SSE2's, as native x86-64 does.
#
# tests/c/x86-sse-conformance.c calls every helper by its LLVM name on random and special inputs, at every immediate,
# and tests/c/x86-headers.py writes a program that calls every intrinsic of Clang's SSE to SSE4.2 headers. Both must
# print what the native instructions print: every bit where the helpers run the instructions (the JIT on this host, with
# and without AVX, NativeAOT at the baseline, whose SSE4.2 is opportunistic, and at x86-64-v3, system mode, 256-bit
# fragments), and with NaN results canonicalized where the compositions run (SSE4.2 or all hardware intrinsics turned
# off, and at -O2, where LLVM itself rewrites dpps and dppd natively), because RyuJIT may swap the operands of a
# floating-point addition or multiplication, which picks the NaN payload when both are NaN. ARM64 hosts run the
# conformance program from the xp bundle (tests/xp/fixtures.tsv). tests/c/x86-avx-conformance.c and the v3 level of
# x86-headers.py do the same for x86-64-v3, where FMA's NaN payloads are compared canonicalized too: RyuJIT picks the
# FMA instruction form by register allocation, and the form decides which of several NaN operands propagates.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
status() { local code=0; "$@" >/dev/null 2>&1 || code=$?; echo "$code"; }
portable_column() { cut -d' ' -f1,3 "$1"; }
absent() { if grep -aqE "$1" "$2"; then printf 'FAIL: %s matches %s\n' "$2" "$1" >&2; exit 1; fi; }

conformance="$root/tests/c/x86-sse-conformance.c"
clang-22 -O0 -march=x86-64-v2 "$conformance" -lm -o native-O0
clang-22 -O2 -march=x86-64-v2 "$conformance" -lm -o native-O2
./native-O0 >native-O0.txt
./native-O2 >native-O2.txt
# Every helper the runtime has is reached, and the program uses no x86 intrinsic without one.
grep -o 'LlvmIntrinsic("[^"]*")' "$root/src/LlvmNet.Runtime/Simd/X86.cs" | sed 's/^LlvmIntrinsic("//; s/")$//' | grep -v '^llvm.x86.mmx.emms$' | sort -u >helpers.txt
"$llvmnet" --isa=x86-64-v2 -O0 -S -c "$conformance" -o conformance.ll
grep -o '@llvm\.x86\.[a-z0-9.]*' conformance.ll | sed 's/^@//' | sort -u >used.txt
if ! diff -u helpers.txt used.txt; then
    printf 'FAIL: the conformance program and the runtime helpers cover different intrinsics\n' >&2
    exit 1
fi
"$llvmnet" --isa=x86-64-v2 -O0 "$conformance" -o portable-O0.dll
"$llvmnet" --isa=x86-64-v2 -O2 "$conformance" -o portable-O2.dll
"$llvmnet" --isa=x86-64-v2 -O0 --vector-width=256 "$conformance" -o portable-256.dll
"$llvmnet" --runtime=system --isa=x86-64-v2 -O0 "$conformance" -o system-O0.dll
dotnet portable-O0.dll | diff -u native-O0.txt -
DOTNET_EnableAVX=0 dotnet portable-O0.dll | diff -u native-O0.txt -
dotnet portable-256.dll | diff -u native-O0.txt -
dotnet system-O0.dll | diff -u native-O0.txt -
for knob in DOTNET_EnableSSE42=0 DOTNET_EnableHWIntrinsic=0; do
    env "$knob" dotnet portable-O0.dll >"composed-$knob.txt"
    diff -u <(portable_column native-O0.txt) <(portable_column "composed-$knob.txt")
done
dotnet portable-O2.dll >portable-O2.txt
diff -u <(portable_column native-O2.txt) <(portable_column portable-O2.txt)
# NativeAOT at the default for x86-64-v2 objects (ILC's x86-64-v2), at the baseline (SSE4.2 tested at run time) and v3.
"$llvmnet" --isa=x86-64-v2 -O0 --nativeaot "$conformance" -o aot-v2 >aot-v2.log 2>&1
./aot-v2 | diff -u native-O0.txt -
"$llvmnet" --isa=x86-64-v2 -O0 --nativeaot --aot-instruction-set baseline "$conformance" -o aot-baseline >aot-baseline.log 2>&1
./aot-baseline | diff -u native-O0.txt -
"$llvmnet" --isa=x86-64-v2 -O0 --nativeaot --aot-instruction-set x86-64-v3 "$conformance" -o aot-v3 >aot-v3.log 2>&1
./aot-v3 | diff -u native-O0.txt -
printf 'PASS: %d SSE to SSE4.2 and PCLMULQDQ intrinsics match the native instructions under the JIT, NativeAOT and with their compositions\n' "$(wc -l <helpers.txt)"

# Clang's SSE to SSE4.2 headers: every intrinsic, the generic ones through the typed vector lowering.
python3 "$root/tests/c/x86-headers.py" clang-22 headers.c
clang-22 -O0 -march=x86-64-v2 -w headers.c -o headers-native-O0
clang-22 -O2 -march=x86-64-v2 -w headers.c -o headers-native-O2
./headers-native-O0 >headers-native-O0.txt
./headers-native-O2 >headers-native-O2.txt
"$llvmnet" --isa=x86-64-v2 -O0 -w headers.c -o headers-O0.dll
"$llvmnet" --isa=x86-64-v2 -O2 -w headers.c -o headers-O2.dll
dotnet headers-O0.dll | diff -u headers-native-O0.txt -
dotnet headers-O2.dll >headers-O2.txt
diff -u <(portable_column headers-native-O2.txt) <(portable_column headers-O2.txt)
DOTNET_EnableHWIntrinsic=0 dotnet headers-O0.dll >headers-software.txt
diff -u <(portable_column headers-native-O0.txt) <(portable_column headers-software.txt)
"$llvmnet" --isa=x86-64-v2 -O1 -w --nativeaot headers.c -o headers-aot >headers-aot.log 2>&1
clang-22 -O1 -march=x86-64-v2 -w headers.c -o headers-native-O1
./headers-native-O1 >headers-native-O1.txt
./headers-aot >headers-aot.txt
diff -u <(portable_column headers-native-O1.txt) <(portable_column headers-aot.txt)
printf 'PASS: the %d intrinsics of the SSE to SSE4.2 headers match native code\n' "$(wc -l <headers-native-O0.txt)"

# x86-64-v3: every helper of AVX, AVX2, FMA, F16C and BMI by its LLVM name, and every intrinsic of the v3 headers, at
# the 256-bit fragments the level implies and at 128 bits.
avx="$root/tests/c/x86-avx-conformance.c"
clang-22 -O0 -march=x86-64-v3 -w "$avx" -lm -o avx-native-O0
clang-22 -O2 -march=x86-64-v3 -w "$avx" -lm -o avx-native-O2
./avx-native-O0 >avx-native-O0.txt
./avx-native-O2 >avx-native-O2.txt
grep -o 'LlvmIntrinsic("[^"]*")' "$root/src/LlvmNet.Runtime/Simd/X86Avx.cs" | sed 's/^LlvmIntrinsic("//; s/")$//' | sort -u >helpers-v3.txt
"$llvmnet" --isa=x86-64-v3 -O0 -w -S -c "$avx" -o avx-conformance.ll
grep -o '@llvm\.x86\.[a-z0-9.]*' avx-conformance.ll | sed 's/^@//' | sort -u | grep -E '^llvm\.x86\.(avx|avx2|fma|bmi|vcvtps2ph)\.' >used-v3.txt
if ! diff -u helpers-v3.txt used-v3.txt; then
    printf 'FAIL: the AVX conformance program and the x86-64-v3 helpers cover different intrinsics\n' >&2
    exit 1
fi
"$llvmnet" --isa=x86-64-v3 -O0 -w "$avx" -o avx-O0.dll
"$llvmnet" --isa=x86-64-v3 -O2 -w "$avx" -o avx-O2.dll
"$llvmnet" --isa=x86-64-v3 -O0 -w --vector-width=128 "$avx" -o avx-128.dll
"$llvmnet" --runtime=system --isa=x86-64-v3 -O0 -w "$avx" -o avx-system.dll
dotnet avx-O0.dll >avx-O0.txt
diff -u <(grep -v '^fmaddsub ' avx-native-O0.txt) <(grep -v '^fmaddsub ' avx-O0.txt)
for run in "dotnet avx-128.dll" "dotnet avx-system.dll" "env DOTNET_EnableAVX2=0 dotnet avx-O0.dll" "env DOTNET_EnableAVX=0 dotnet avx-O0.dll" \
    "env DOTNET_EnableFMA=0 dotnet avx-O0.dll" "env DOTNET_EnableBMI2=0 dotnet avx-O0.dll" "env DOTNET_EnableHWIntrinsic=0 dotnet avx-O0.dll"; do
    # shellcheck disable=SC2086
    $run >avx-run.txt
    diff -u <(portable_column avx-native-O0.txt) <(portable_column avx-run.txt)
done
dotnet avx-O2.dll >avx-O2.txt
diff -u <(portable_column avx-native-O2.txt) <(portable_column avx-O2.txt)
"$llvmnet" --isa=x86-64-v3 -O0 -w --nativeaot "$avx" -o avx-aot >avx-aot.log 2>&1
./avx-aot >avx-aot.txt
diff -u <(portable_column avx-native-O0.txt) <(portable_column avx-aot.txt)
"$llvmnet" --isa=x86-64-v3 -O0 -w --nativeaot --aot-instruction-set baseline "$avx" -o avx-aot-baseline >avx-aot-baseline.log 2>&1
./avx-aot-baseline >avx-aot-baseline.txt
diff -u <(portable_column avx-native-O0.txt) <(portable_column avx-aot-baseline.txt)
printf 'PASS: %d x86-64-v3 intrinsics match the native instructions under the JIT, NativeAOT and with their compositions\n' "$(wc -l <helpers-v3.txt)"
python3 "$root/tests/c/x86-headers.py" clang-22 headers-v3.c v3
clang-22 -O0 -march=x86-64-v3 -w headers-v3.c -o headers-v3-native-O0
clang-22 -O2 -march=x86-64-v3 -w headers-v3.c -o headers-v3-native-O2
./headers-v3-native-O0 >headers-v3-native-O0.txt
./headers-v3-native-O2 >headers-v3-native-O2.txt
"$llvmnet" --isa=x86-64-v3 -O0 -w headers-v3.c -o headers-v3-O0.dll
"$llvmnet" --isa=x86-64-v3 -O2 -w headers-v3.c -o headers-v3-O2.dll
for run in "dotnet headers-v3-O0.dll" "env DOTNET_EnableAVX=0 dotnet headers-v3-O0.dll" "env DOTNET_EnableHWIntrinsic=0 dotnet headers-v3-O0.dll"; do
    # shellcheck disable=SC2086
    $run >headers-v3-run.txt
    diff -u <(portable_column headers-v3-native-O0.txt) <(portable_column headers-v3-run.txt)
done
dotnet headers-v3-O2.dll >headers-v3-O2.txt
diff -u <(portable_column headers-v3-native-O2.txt) <(portable_column headers-v3-O2.txt)
"$llvmnet" --isa=x86-64-v3 -O1 -w --nativeaot headers-v3.c -o headers-v3-aot >headers-v3-aot.log 2>&1
clang-22 -O1 -march=x86-64-v3 -w headers-v3.c -o headers-v3-native-O1
./headers-v3-native-O1 >headers-v3-native-O1.txt
./headers-v3-aot >headers-v3-aot.txt
diff -u <(portable_column headers-v3-native-O1.txt) <(portable_column headers-v3-aot.txt)
printf 'PASS: the %d intrinsics of the x86-64-v3 headers match native code\n' "$(wc -l <headers-v3-native-O0.txt)"

# --isa: the macros of each level, the level recorded in objects and images, and objects of both levels linked together.
# A pipe from macros ends in grep without -q, which reads all of it: grep -q exits at the first match, and the
# preprocessor's next write then fails with a broken pipe, which pipefail turns into a failure under load.
macros() { "$llvmnet" "$@" -dM -E -x c /dev/null; }
macros --isa=x86-64-v2 >v2-macros.txt
grep -q '^#define __SSE4_2__ 1' v2-macros.txt
grep -q '^#define __x86_64__ 1' v2-macros.txt
grep -q '^#define __POPCNT__ 1' v2-macros.txt
absent 'GCC_HAVE_SYNC_COMPARE_AND_SWAP_16' v2-macros.txt
macros >baseline-macros.txt
absent '__SSE2__|__x86_64__|__MMX__' baseline-macros.txt
macros --runtime=system | grep '^#define __SSE2__ 1' >/dev/null
macros --runtime=system --isa=x86-64-v2 | grep '^#define __SSE4_1__ 1' >/dev/null
# Portable mode takes the x86 SIMD feature flags up to SSE4.2 as aliases of --isa=x86-64-v2, passed on to Clang (so
# -mpclmul adds PCLMULQDQ); the negations above SSE2 change nothing else, and AVX's flags and -mno-sse2 stay rejected.
macros -msse4.1 >alias-macros.txt
grep -q '^#define __SSE4_2__ 1' alias-macros.txt
macros -mpclmul | grep '^#define __PCLMUL__ 1' >/dev/null
macros -mno-sse4.1 >negation-macros.txt
absent '__SSE2__' negation-macros.txt
for flag in -mavx512f -mno-sse2; do
    code=$(status "$llvmnet" "$flag" -c -x c /dev/null -o rejected.o)
    test "$code" -eq 2
done
"$llvmnet" -mavx512f -c -x c /dev/null -o rejected.o 2>avx512-flag.err || true
grep -q 'selects AVX-512, whose intrinsics the runtime does not implement' avx512-flag.err
# x86-64-v3 and its aliases, whose level wins over x86-64-v2's in either order.
macros --isa=x86-64-v3 >v3-macros.txt
for macro in __AVX2__ __AVX__ __FMA__ __F16C__ __BMI__ __BMI2__ __LZCNT__ __SSE4_2__; do grep -q "^#define $macro 1" v3-macros.txt; done
absent 'GCC_HAVE_SYNC_COMPARE_AND_SWAP_16|__AVX512' v3-macros.txt
macros -mavx2 | grep '^#define __AVX2__ 1' >/dev/null
macros -mavx2 -msse4.2 | grep '^#define __AVX2__ 1' >/dev/null
macros -mfma | grep '^#define __FMA__ 1' >/dev/null
printf '#include <nmmintrin.h>\nunsigned crc(unsigned c, unsigned d) { return _mm_crc32_u32(c, d); }\n' >crc.c
printf '#include <stdio.h>\nunsigned crc(unsigned, unsigned);\nint main(void) { printf("%%u\\n", crc(1, 2)); return 0; }\n' >main.c
"$llvmnet" -msse4.2 -O1 -c crc.c -o crc.o
"$llvmnet" -O1 -c main.c -o main.o
"$llvmnet" crc.o main.o -o mixed.dll
test "$(dotnet mixed.dll)" = "$(printf '#include <stdio.h>\n#include <nmmintrin.h>\nint main(void) { printf("%%u\\n", _mm_crc32_u32(1, 2)); return 0; }\n' | clang-22 -march=x86-64-v2 -x c - -o crc-native && ./crc-native)"
grep -aq 'llvmnet.isa' mixed.dll
grep -aq 'x86-64-v2' mixed.dll
"$llvmnet" -O1 "$root/tests/c/x86-hints.c" -o baseline.dll
absent 'llvmnet.isa' baseline.dll
# NativeAOT takes ILC's x86-64-v2 for a program with x86-64-v2 objects (on x64 identifiers), the baseline for scalar code.
"$llvmnet" crc.o main.o --aot-project mixed-aot >/dev/null
grep -q '>x86-64-v2</IlcInstructionSet>' mixed-aot/NativeHost.csproj
"$llvmnet" -O1 "$root/tests/c/x86-hints.c" --aot-project baseline-aot >/dev/null
absent 'x86-64-v2' baseline-aot/NativeHost.csproj
# ... and x86-64-v3 for a program with x86-64-v3 objects, whatever the other objects' level.
printf 'int nothing(void) { return 0; }\n' >v3.c
"$llvmnet" --isa=x86-64-v3 -O1 -c v3.c -o v3.o
"$llvmnet" crc.o main.o v3.o --aot-project v3-aot >/dev/null
grep -q '>x86-64-v3</IlcInstructionSet>' v3-aot/NativeHost.csproj
# ... and x86-64-v2 for a program with vector code, which the vectorizers' widenings and multiplications need (the
# owner's SV-05 decision); built scalar, or with the baseline asked for, the same program keeps the baseline.
printf '#include <stdio.h>\nstatic float a[1024];\nint main(int c, char **v) { for (int i = 0; i < 1024; i++) a[i] = i * (float)c * 2.0f + 1.0f; printf("%%g\\n", a[1023]); return 0; }\n' >scale.c
"$llvmnet" -O2 -v scale.c --aot-project vector-aot >/dev/null 2>vector-aot.log
grep -q '>x86-64-v2</IlcInstructionSet>' vector-aot/NativeHost.csproj
grep -q 'NativeAOT targets x86-64-v2 (SSE4.2), because the program has vector code' vector-aot.log
"$llvmnet" -O2 -fno-vectorize -fno-slp-vectorize scale.c --aot-project scalar-aot >/dev/null
absent 'x86-64-v2' scalar-aot/NativeHost.csproj
"$llvmnet" -O2 --aot-instruction-set baseline scale.c --aot-project forced-aot >/dev/null
absent 'x86-64-v2' forced-aot/NativeHost.csproj
printf 'PASS: --isa=x86-64-v2 and its -m aliases define the x86 macros, are recorded in the image and link with baseline objects\n'

# A library's SSE2 switch: system mode and --isa=x86-64-v2 take the SSE2 path, which now translates; the portable
# baseline and -U__SSE2__ take the C path.
sse="$root/tests/c/x86-sse-path.c"
clang-22 -O2 "$sse" -o sse-native
./sse-native >sse-native.txt
for build in "--runtime=system" "--runtime=portable --isa=x86-64-v2" "--runtime=portable" "--runtime=system -U__SSE__ -U__SSE2__"; do
    # shellcheck disable=SC2086
    "$llvmnet" $build -O2 "$sse" -o sse.dll 2>sse.err
    test ! -s sse.err
    dotnet sse.dll | diff -u sse-native.txt -
done
"$llvmnet" --runtime=system -O2 "$sse" -o sse-system.dll
"$llvmnet" --runtime=system -O2 -U__SSE__ -U__SSE2__ "$sse" -o sse-c.dll
grep -aq Psadbw sse-system.dll
printf 'PASS: SSE2 paths translate in system mode and at --isa=x86-64-v2; the portable baseline keeps the C path\n'

# Intrinsics without a helper (the MXCSR write, AVX and later) warn when their unit is compiled (-w silences it) and
# are rejected when a program that reaches them is linked; a unit whose x86 code no program reaches still links.
printf '#include <xmmintrin.h>\nvoid flush_to_zero(void) { _mm_setcsr(_mm_getcsr() | 0x8040); }\nint main(void) { flush_to_zero(); return 0; }\n' >csr.c
"$llvmnet" --isa=x86-64-v2 -O1 -c csr.c -o csr.o 2>csr.err
grep -q '^llvmnet: warning: x86 target intrinsic llvm.x86.sse.ldmxcsr is not translated, so linking a program that reaches it fails' csr.err
grep -q "^llvmnet: note: in function '\(flush_to_zero\|main\)' (compile with -g" csr.err
"$llvmnet" --isa=x86-64-v2 -O1 -g -c csr.c -o csr-g.o 2>csr-g.err
grep -q -E '^[^ ]*csr\.c:[0-9]+:[0-9]+: warning: x86 target intrinsic llvm\.x86\.sse\.ldmxcsr' csr-g.err
"$llvmnet" --isa=x86-64-v2 -O1 -w -c csr.c -o csr-w.o 2>csr-w.err
test ! -s csr-w.err
code=$(status "$llvmnet" csr.o -o csr.dll)
test "$code" -eq 1
"$llvmnet" csr.o -o csr.dll 2>csr-link.err || true
grep -q '^llvmnet: error: x86 target intrinsic llvm.x86.sse.ldmxcsr is not translated' csr-link.err
printf '#include <immintrin.h>\n__attribute__((target("avx512bw"))) int sad64(const void *a, const void *b) { __m512i s = _mm512_sad_epu8(_mm512_loadu_si512(a), _mm512_loadu_si512(b)); return _mm_cvtsi128_si32(_mm512_castsi512_si128(s)); }\nint main(void) { return 0; }\n' >avx512.c
"$llvmnet" --runtime=system -O1 avx512.c -o avx512.dll 2>avx512.err
grep -q 'warning: x86 target intrinsic llvm.x86.avx512.psad.bw.512 is not translated' avx512.err
dotnet avx512.dll
printf 'PASS: intrinsics without a helper warn while compiling, fail at the link when reached and link when unreached\n'

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
