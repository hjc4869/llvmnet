/* CPU detection the way libraries do it (SV-12): <cpuid.h>, XGETBV, compiler-rt's CPU model behind
   __builtin_cpu_supports and __builtin_cpu_is, a target_clones function and an ifunc. Built for
   x86-64-v2 (natively -march=x86-64-v2, portable --isa=x86-64-v2), it must print what native code prints on an x64
   host, and report no x86 feature where .NET runs no x86 instructions. Leaf 1's EBX holds the APIC id of the core the
   thread runs on, so it is not printed. With the argument "consistency" it prints only what holds on every host: that
   CPUID, the CPU model, <llvmnet/cpu.h> and the dispatch agree with one another. */
#include <cpuid.h>
#include <stdio.h>
#include <string.h>
#ifdef __LLVMNET__
#include <llvmnet/cpu.h>
#endif

__attribute__((target_clones("avx2", "sse4.2", "default"))) static long sum(const int *values, int count)
{
    long total = 0;
    for (int index = 0; index < count; index++) total += values[index] * (long)(index & 7);
    return total;
}

static int narrow(void) { return 1; }
static int wide(void) { return 2; }
static int (*resolve(void))(void)
{
    __builtin_cpu_init();
    return __builtin_cpu_supports("avx2") ? wide : narrow;
}
#ifdef __linux__
/* The portable target is not Linux, where Clang ignores the ifunc attribute; the resolver is called directly there. */
static int width(void) __attribute__((ifunc("resolve")));
#endif

/* SSE4.2 and AVX2 as CPUID and XGETBV report them, the way a library tests them. */
static void cpuid_features(int *sse4_2, int *avx2)
{
    unsigned eax, ebx, ecx, edx, maximum = __get_cpuid_max(0, 0);
    *sse4_2 = *avx2 = 0;
    if (maximum < 1 || !__get_cpuid(1, &eax, &ebx, &ecx, &edx)) return;
    *sse4_2 = (ecx & bit_SSE4_2) != 0;
    if (!(ecx & bit_OSXSAVE) || maximum < 7) return;
    unsigned low, high;
    __asm__(".byte 0x0f, 0x01, 0xd0" : "=a"(low), "=d"(high) : "c"(0));
    __get_cpuid_count(7, 0, &eax, &ebx, &ecx, &edx);
    *avx2 = (low & 6) == 6 && (ebx & bit_AVX2) != 0;
}

static int consistency(void)
{
    int sse4_2, avx2;
    cpuid_features(&sse4_2, &avx2);
    __builtin_cpu_init();
    printf("cpuid and cpu model agree: %s\n", sse4_2 == !!__builtin_cpu_supports("sse4.2") && avx2 == !!__builtin_cpu_supports("avx2") ? "yes" : "no");
#ifdef __LLVMNET__
    unsigned long long features = llvmnet_cpu_features();
    printf("llvmnet_cpu_features: %s\n", !!(features & LLVMNET_CPU_SSE4_2) == sse4_2 && !!(features & LLVMNET_CPU_AVX2) == avx2 ? "agrees" : "differs");
#else
    printf("llvmnet_cpu_features: agrees\n");
#endif
    int values[1000];
    for (int index = 0; index < 1000; index++) values[index] = index * 3 - 500;
    printf("target_clones: %ld\n", sum(values, 1000));
#ifdef __linux__
    int chosen = width();
#else
    int chosen = resolve()();
#endif
    printf("ifunc: %s\n", chosen == (avx2 ? 2 : 1) ? "follows avx2" : "wrong");
    return 0;
}

int main(int argc, char **argv)
{
    if (argc > 1 && strcmp(argv[1], "consistency") == 0) return consistency();
    unsigned eax = 0, ebx = 0, ecx = 0, edx = 0, maximum = __get_cpuid_max(0, 0);
    printf("cpuid: %s\n", maximum >= 1 ? "yes" : "no");
    if (__get_cpuid(1, &eax, &ebx, &ecx, &edx)) {
        printf("leaf 1: eax=%08x ecx=%08x edx=%08x\n", eax, ecx, edx);
        if (ecx & bit_OSXSAVE) {
            unsigned low, high;
            __asm__(".byte 0x0f, 0x01, 0xd0" : "=a"(low), "=d"(high) : "c"(0));
            printf("xcr0: %08x%08x\n", high, low);
        }
    }
    if (maximum >= 7 && __get_cpuid_count(7, 0, &eax, &ebx, &ecx, &edx))
        printf("leaf 7: eax=%08x ebx=%08x ecx=%08x edx=%08x\n", eax, ebx, ecx, edx);

    __builtin_cpu_init();
    unsigned supported = 0, bit = 0;
#define FEATURE(name) supported |= (__builtin_cpu_supports(name) ? 1u : 0u) << bit++;
    FEATURE("sse2") FEATURE("sse3") FEATURE("ssse3") FEATURE("sse4.1") FEATURE("sse4.2") FEATURE("popcnt") FEATURE("pclmul")
    FEATURE("aes") FEATURE("avx") FEATURE("avx2") FEATURE("fma") FEATURE("bmi") FEATURE("bmi2") FEATURE("avx512f")
    FEATURE("avx512bw") FEATURE("avx512vl")
    printf("cpu_supports: %04x\n", supported);
    printf("cpu_is: amd=%d intel=%d\n", __builtin_cpu_is("amd") != 0, __builtin_cpu_is("intel") != 0);

    int values[1000];
    for (int index = 0; index < 1000; index++) values[index] = index * 3 - 500;
    printf("target_clones: %ld\n", sum(values, 1000));
#ifdef __linux__
    printf("ifunc: %d\n", width());
#else
    printf("ifunc: %d\n", resolve()());
#endif
#ifdef __LLVMNET__
    /* <llvmnet/cpu.h> agrees with the CPU model, which reads CPUID. */
    unsigned long long features = llvmnet_cpu_features();
    int agrees = !!(features & LLVMNET_CPU_SSE4_2) == !!__builtin_cpu_supports("sse4.2") &&
                 !!(features & LLVMNET_CPU_AVX2) == !!__builtin_cpu_supports("avx2") &&
                 !!(features & LLVMNET_CPU_PCLMUL) == !!__builtin_cpu_supports("pclmul") && llvmnet_native_kernels("any") == 0;
    printf("llvmnet_cpu_features: %s\n", agrees ? "agrees" : "differs");
#else
    printf("llvmnet_cpu_features: agrees\n");
#endif
    return 0;
}
