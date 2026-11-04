#!/usr/bin/env bash
# tags: core aot
# System mode's native objects (ADR-16 Level 3, SV-15): the synthetic kernels' x86-64 assembly given as a .S source, as
# an object llvmnet -c assembled, and as a member of an archive beside the program's bitcode, and a nasm kernel given as
# .asm, are linked into the image's native library (and NativeAOT's static link) and called from translated code; the
# kernels and the program share data both ways. Each must print what the native program prints. A native object that
# calls a function of the program, or references a symbol nothing defines, is a compile error naming it, as are assembly
# and native objects in portable mode without a kernel package.
# The images have distinct names: one process (tests/jit-validation.sh) resolves a second lib<name>.llvmnet-host.so to the
# first one it loaded.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
kernels="$root/tests/kernels/synthetic"
clang-22 -O1 "$kernels/program.c" "$kernels/x86_64.S" -o native
./native >native.txt
"$llvmnet" --runtime=system -O1 "$kernels/program.c" "$kernels/x86_64.S" -o source/source.dll
test -f source/libsource.llvmnet-host.so
dotnet source/source.dll kernels | diff -u native.txt -
"$llvmnet" --runtime=system -c "$kernels/x86_64.S" -o kernels.o
file kernels.o | grep -q 'ELF 64-bit LSB relocatable, x86-64'
"$llvmnet" --runtime=system -O1 -c "$kernels/program.c" -o program.o
"$llvmnet" --runtime=system -O1 program.o kernels.o -o objects/objects.dll
dotnet objects/objects.dll kernels | diff -u native.txt -
llvm-ar-22 rcs mixed.a program.o kernels.o
"$llvmnet" --runtime=system -O1 mixed.a -o archive/archive.dll
dotnet archive/archive.dll kernels | diff -u native.txt -
"$llvmnet" --runtime=system -O1 "$kernels/program.c" "$kernels/x86_64.S" --nativeaot -o aot/program >aot.log 2>&1
./aot/program kernels | diff -u native.txt -
if ldd aot/program | grep -q llvmnet-host; then printf 'FAIL: the NativeAOT executable loads the native library instead of linking it\n' >&2; exit 1; fi
printf 'PASS: assembly, objects and archive members run in system mode under the JIT and NativeAOT\n'

if command -v nasm >/dev/null; then
    nasm_kernel="$root/tests/kernels/nasm"
    "$llvmnet" --runtime=system -O1 -DNK_SCALE=3 "$nasm_kernel/program.c" "$nasm_kernel/add.asm" -o nasm/nasm.dll
    test "$(dotnet nasm/nasm.dll)" = "3033 1005"
    printf 'PASS: a nasm source runs in system mode with its -D options\n'
fi

printf '    .text\n    .globl call_back\ncall_back:\n    jmp helper\n' >callback.S
printf 'int helper(void) { return 1; }\nint call_back(void);\nint main(void) { return call_back() + helper(); }\n' >callback.c
"$llvmnet" --runtime=system callback.c callback.S -o callback.dll 2>callback.err || true
grep -q 'native object callback.S calls helper, a function of the program' callback.err
printf '    .text\n    .globl uses_missing\nuses_missing:\n    mov missing_table(%%rip), %%eax\n    ret\n' >missing.S
printf 'int uses_missing(void);\nint main(void) { return uses_missing(); }\n' >missing.c
"$llvmnet" --runtime=system missing.c missing.S -o missing.dll 2>missing.err || true
grep -q 'native objects reference missing_table, which neither they, the program nor a system library defines' missing.err
"$llvmnet" -O1 "$kernels/program.c" "$kernels/x86_64.S" -o portable.dll 2>portable.err || true
grep -q 'x86_64.S is assembly, which llvmnet does not translate; portable mode calls assembled code through a native kernel package' portable.err
"$llvmnet" -O1 "$kernels/program.c" kernels.o -o portable-object.dll 2>portable-object.err || true
grep -q 'kernels.o is a native object file, such as assembled code' portable-object.err
printf 'PASS: callbacks into the program, undefined symbols and portable-mode assembly are rejected with their reasons\n'
