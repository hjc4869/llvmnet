#!/usr/bin/env bash
# tags: core aot
# CPU detection without assembly (SV-12). Portable code's CPUID and XGETBV inline assembly (the spellings of
# <cpuid.h>, ffmpeg, LLVM and Eigen) answers from the runtime: the host's CPUID on x64 and nothing where .NET runs no x86
# instructions, with XCR0's AVX and AVX-512 state only where .NET uses them. compiler-rt's CPU model is in both sysroots,
# so __builtin_cpu_supports, __builtin_cpu_is and target_clones link and answer; system mode resolves ifuncs in a
# constructor; <llvmnet/cpu.h> reports the same view, and portable programs start with the host's AT_HWCAP.
#
# tests/c/cpu-detection.c must print what native code prints on this host: under the JIT in both modes, as NativeAOT in
# system mode and at --aot-instruction-set native. NativeAOT's default for x86-64-v2 objects runs no AVX, so it must
# print what the JIT prints with DOTNET_EnableAVX=0. With hardware intrinsics off it must find no x86 feature, and its
# consistency mode, which the xp bundle runs on every host, must hold everywhere.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
fixture="$root/tests/c/cpu-detection.c"
clang-22 -O2 -march=x86-64-v2 "$fixture" -o native
./native >native.txt
./native consistency >consistency.txt
"$llvmnet" --isa=x86-64-v2 -O2 "$fixture" -o portable.dll
"$llvmnet" --runtime=system --isa=x86-64-v2 -O2 "$fixture" -o system-mode.dll
dotnet portable.dll | diff -u native.txt -
dotnet system-mode.dll | diff -u native.txt -
DOTNET_EnableHWIntrinsic=0 dotnet portable.dll >none.txt
diff -u - none.txt <<'EOF'
cpuid: no
cpu_supports: 0000
cpu_is: amd=0 intel=0
target_clones: 3510500
ifunc: 1
llvmnet_cpu_features: agrees
EOF
DOTNET_EnableAVX=0 dotnet portable.dll >without-avx.txt
grep -q '^cpu_supports: ' without-avx.txt
for environment in DOTNET_EnableHWIntrinsic=1 DOTNET_EnableHWIntrinsic=0 DOTNET_EnableAVX=0; do
    env "$environment" dotnet portable.dll consistency | diff -u consistency.txt -
    env "$environment" dotnet system-mode.dll consistency | diff -u consistency.txt -
done
"$llvmnet" --isa=x86-64-v2 -O2 --nativeaot "$fixture" -o portable-aot >portable-aot.log 2>&1
./portable-aot | diff -u without-avx.txt -
"$llvmnet" --isa=x86-64-v2 -O2 --nativeaot --aot-instruction-set native "$fixture" -o portable-aot-native >portable-aot-native.log 2>&1
./portable-aot-native | diff -u native.txt -
"$llvmnet" --runtime=system --isa=x86-64-v2 -O2 --nativeaot "$fixture" -o system-aot >system-aot.log 2>&1
./system-aot | diff -u native.txt -
printf 'PASS: CPUID, XGETBV, __builtin_cpu_supports, target_clones and ifuncs report the host as native code does, and nothing without x86\n'

# The auxiliary vector: AT_HWCAP and AT_HWCAP2 as Linux gives them to native processes (musl's getauxval returns them).
printf '#include <stdio.h>\n#include <sys/auxv.h>\nint main(void) { printf("AT_HWCAP: 0x%%lx\\nAT_HWCAP2: 0x%%lx\\n", getauxval(AT_HWCAP), getauxval(AT_HWCAP2)); return 0; }\n' >auxv.c
"$llvmnet" -O1 auxv.c -o auxv.dll
LD_SHOW_AUXV=1 /bin/true | grep -E '^AT_HWCAP2?:' | sed -E 's/: +/: /' >kernel-auxv.txt
dotnet auxv.dll | diff -u kernel-auxv.txt -
test "$(DOTNET_EnableHWIntrinsic=0 dotnet auxv.dll | tr '\n' ' ')" = "AT_HWCAP: 0x0 AT_HWCAP2: 0x0 "
printf 'PASS: portable programs start with the AT_HWCAP and AT_HWCAP2 Linux gives native processes\n'
