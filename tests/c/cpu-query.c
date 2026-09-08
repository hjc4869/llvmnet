#include <stdio.h>

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
    query(1, 0, registers);
    if ((registers[2] & (1u << 27)) != 0) {
        unsigned lower;
        unsigned upper;
        __asm__(".byte 0x0f, 0x01, 0xd0" : "=a"(lower), "=d"(upper) : "c"(0));
        printf("xcr0=%08x%08x\n", upper, lower);
    }
    if (maximum >= 7) {
        query(7, 0, registers);
        query_preserving_base(7, 0, preserved);
        for (unsigned index = 0; index < 4; ++index)
            if (preserved[index] != registers[index])
                return 2;
        printf("features=%08x %08x %08x %08x\n", registers[0], registers[1], registers[2], registers[3]);
    }
    return 0;
}