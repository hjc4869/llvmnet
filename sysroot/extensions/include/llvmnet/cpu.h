/* CPU detection without assembly (docs/user/x86-intrinsics.md#cpu-detection).
 *
 * The instruction sets the running host executes, as .NET reports them: in portable mode the same image runs on x64 and
 * ARM64 hosts, so a program that chooses code paths at run time can ask here instead of executing CPUID. CPUID and XGETBV
 * inline assembly, <cpuid.h>, __builtin_cpu_supports and getauxval(AT_HWCAP) answer from the same view. These functions
 * are part of LlvmNet.Runtime and of every llvmnet toolchain, in both runtime modes; the header is outside the frozen
 * dotnet64-v2 header set. */
#ifndef LLVMNET_CPU_H
#define LLVMNET_CPU_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* x86 instruction sets, in the low half. */
#define LLVMNET_CPU_SSE2 (1ull << 0)
#define LLVMNET_CPU_SSE3 (1ull << 1)
#define LLVMNET_CPU_SSSE3 (1ull << 2)
#define LLVMNET_CPU_SSE4_1 (1ull << 3)
#define LLVMNET_CPU_SSE4_2 (1ull << 4)
#define LLVMNET_CPU_POPCNT (1ull << 5)
#define LLVMNET_CPU_PCLMUL (1ull << 6)
#define LLVMNET_CPU_AES (1ull << 7)
#define LLVMNET_CPU_AVX (1ull << 8)
#define LLVMNET_CPU_AVX2 (1ull << 9)
#define LLVMNET_CPU_FMA (1ull << 10)
#define LLVMNET_CPU_BMI1 (1ull << 11)
#define LLVMNET_CPU_BMI2 (1ull << 12)
#define LLVMNET_CPU_LZCNT (1ull << 13)
#define LLVMNET_CPU_AVX512F (1ull << 14)
#define LLVMNET_CPU_AVX512BW (1ull << 15)

/* ARM64 instruction sets, in the high half. */
#define LLVMNET_CPU_NEON (1ull << 32)
#define LLVMNET_CPU_ARM_AES (1ull << 33)
#define LLVMNET_CPU_CRC32 (1ull << 34)
#define LLVMNET_CPU_DOTPROD (1ull << 35)
#define LLVMNET_CPU_RDM (1ull << 36)
#define LLVMNET_CPU_SHA1 (1ull << 37)
#define LLVMNET_CPU_SHA256 (1ull << 38)
#define LLVMNET_CPU_SVE (1ull << 39)

/* The LLVMNET_CPU_* bits of the instruction sets the host executes; 0 when .NET's hardware intrinsics are turned off
 * (DOTNET_EnableHWIntrinsic=0). The x86 intrinsics llvmnet translates run on every host, as instructions where these bits
 * say so and as compositions elsewhere. */
uint64_t llvmnet_cpu_features(void);

/* 1 when the running host has a library of the native kernel package NAME, 0 otherwise; kernel packages are not
 * implemented yet, so 0 for every name. */
int llvmnet_native_kernels(const char *name);

#ifdef __cplusplus
}
#endif

#endif
