#include <stdio.h>
#ifdef __x86_64__
#include <cpuid.h>
#endif

static void query(unsigned leaf, unsigned subleaf, unsigned registers[4])
{
    __asm__("movq\t%%rbx, %%rsi\n\tcpuid\n\txchgq\t%%rbx, %%rsi\n\t"
        : "=a"(registers[0]), "=S"(registers[1]), "=c"(registers[2]), "=d"(registers[3])
        : "a"(leaf), "c"(subleaf));
}

static void query_preserving_base(unsigned leaf, unsigned subleaf, unsigned registers[4])
{
    __asm__ __volatile__("xchg{q}\t{%%}rbx, %q1; cpuid; xchg{q}\t{%%}rbx, %q1"
        : "=a"(registers[0]), "=&r"(registers[1]), "=c"(registers[2]), "=d"(registers[3])
        : "0"(leaf), "2"(subleaf));
}

/* ffmpeg's cpuid macro (libavutil/x86/cpu.c with inline assembly), and the plain form that names EBX as an output. */
static void query_ffmpeg(unsigned leaf, unsigned registers[4])
{
    __asm__ volatile("mov %%rbx, %%rsi \n\t cpuid \n\t xchg %%rbx, %%rsi"
        : "=a"(registers[0]), "=S"(registers[1]), "=c"(registers[2]), "=d"(registers[3])
        : "0"(leaf), "2"(0));
}

static void query_plain(unsigned leaf, unsigned registers[4])
{
    __asm__ volatile("cpuid" : "=a"(registers[0]), "=b"(registers[1]), "=c"(registers[2]), "=d"(registers[3]) : "a"(leaf), "c"(0));
}

int main(void)
{
    unsigned registers[4];
    __asm__("movq\t%%rbx, %%rsi\n\tcpuid\n\txchgq\t%%rbx, %%rsi\n\t"
        : "=a"(registers[0]), "=S"(registers[1]), "=c"(registers[2]), "=d"(registers[3])
        : "a"(0));
    unsigned maximum = registers[0];
    printf("vendor=%08x %08x %08x maximum=%x\n", registers[1], registers[3], registers[2], maximum);
    unsigned preserved[4];
    query_preserving_base(0, 0, preserved);
    for (unsigned index = 0; index < 4; ++index)
        if (preserved[index] != registers[index])
            return 1;
    unsigned other[4];
    query_ffmpeg(0, other);
    for (unsigned index = 0; index < 4; ++index)
        if (other[index] != registers[index])
            return 5;
    query_plain(0, other);
    for (unsigned index = 0; index < 4; ++index)
        if (other[index] != registers[index])
            return 6;
    query(1, 0, registers);
    if ((registers[2] & (1u << 27)) != 0) {
        unsigned lower;
        unsigned upper;
        __asm__(".byte 0x0f, 0x01, 0xd0" : "=a"(lower), "=d"(upper) : "c"(0));
        printf("xcr0=%08x%08x\n", upper, lower);
        unsigned mnemonic_lower, mnemonic_upper;
        __asm__("xgetbv" : "=a"(mnemonic_lower), "=d"(mnemonic_upper) : "c"(0));
        if (mnemonic_lower != lower || mnemonic_upper != upper)
            return 7;
    }
#ifdef __x86_64__
    /* Clang's <cpuid.h> macros, which libraries' feature detection uses. */
    unsigned header[4];
    __cpuid(0, header[0], header[1], header[2], header[3]);
    if (header[0] != maximum || header[1] != preserved[1])
        return 3;
#endif
    if (maximum >= 7) {
        query(7, 0, registers);
        query_preserving_base(7, 0, preserved);
        for (unsigned index = 0; index < 4; ++index)
            if (preserved[index] != registers[index])
                return 2;
#ifdef __x86_64__
        __cpuid_count(7, 0, header[0], header[1], header[2], header[3]);
        for (unsigned index = 0; index < 4; ++index)
            if (header[index] != registers[index])
                return 4;
#endif
        printf("features=%08x %08x %08x %08x\n", registers[0], registers[1], registers[2], registers[3]);
    }
    return 0;
}