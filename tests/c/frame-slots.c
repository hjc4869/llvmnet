/* Static allocas, byval copies and the variadic call area in frame slots (PF-24): allocas of every alignment up to 64
 * bytes are aligned, zero-size ones have an address, each recursion level and each inlined call has its own, addresses
 * passed to callees and kept across setjmp and longjmp stay valid, byval copies are each callee's own, and functions
 * with loops (which keep localloc under the JIT) and allocas past the slot limit behave the same. The program prints
 * the same natively and translated. */
#include <setjmp.h>
#include <stdarg.h>
#include <stdalign.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

/* The remainder of an address modulo its alignment, after writing the block; noinline keeps the address in memory. */
__attribute__((noinline)) static int misalignment(unsigned char *buffer, int alignment, int size, int seed)
{
    memset(buffer, seed, size);
    return (int)((uintptr_t)buffer % alignment);
}

#define ALIGNED(n) \
    __attribute__((noinline)) static int aligned##n(int seed) \
    { \
        alignas(n) unsigned char buffer[n + 3]; \
        return misalignment(buffer, n, sizeof buffer, seed) + buffer[n + 2]; \
    }
ALIGNED(1) ALIGNED(2) ALIGNED(4) ALIGNED(8) ALIGNED(16) ALIGNED(32) ALIGNED(64)

__attribute__((noinline)) static void fill(int *values, int count, int base)
{
    for (int index = 0; index < count; index++) values[index] = base + index;
}

/* Each level keeps its own array while deeper levels write theirs. */
__attribute__((noinline)) static int recurse(int depth)
{
    int values[4];
    fill(values, 4, depth * 10);
    int below = depth > 0 ? recurse(depth - 1) : 0;
    return below + values[0] + values[3];
}

/* A zero-size alloca still has an address (flang-rt reads a null one as an unallocated zero-length variable). */
__attribute__((noinline)) static int addressed(void)
{
    char none[0];
    char *volatile address = none;
    return address != NULL;
}

static inline int sum3(int a, int b, int c)
{
    int values[3] = { a, b, c };
    fill(values, a & 1, c);
    return values[0] + values[1] + values[2];
}

__attribute__((noinline)) static int inlined(int rounds)
{
    int total = 0;
    for (int round = 0; round < rounds; round++) total += sum3(round, round * 2, round * 3);
    return total;
}

static jmp_buf target;

__attribute__((noinline)) static void jump(int *value)
{
    *value += 5;
    longjmp(target, 1);
}

__attribute__((noinline)) static int jumped(void)
{
    volatile int kept[2] = { 1, 2 };
    if (setjmp(target) == 0) {
        kept[0] = 40;
        jump((int *)&kept[1]);
    }
    return kept[0] + kept[1];
}

/* A struct passed by value is the callee's own copy, one per recursion level. */
struct record { long values[5]; };

__attribute__((noinline)) static long copies(struct record record, int depth)
{
    record.values[depth % 5] += depth;
    long below = depth > 0 ? copies(record, depth - 1) : 0;
    return below + record.values[0] + record.values[4];
}

/* Past the slot limit, the block stays a localloc. */
__attribute__((noinline)) static int large(int seed)
{
    char buffer[100000];
    memset(buffer, seed, sizeof buffer);
    buffer[99999] = 7;
    return buffer[0] + buffer[50000] + buffer[99999];
}

__attribute__((noinline)) static void report(const char *format, ...)
{
    va_list arguments;
    va_start(arguments, format);
    vprintf(format, arguments);
    va_end(arguments);
}

__attribute__((noinline)) static int checked(int value)
{
    if (value < 0) report("negative %d %g %ld %Lg\n", value, 1.5, 2L, 0.25L);
    return value * 2;
}

int main(void)
{
    printf("aligned %d %d %d %d %d %d %d\n", aligned1(1), aligned2(2), aligned4(3), aligned8(4), aligned16(5), aligned32(6), aligned64(7));
    printf("recurse %d\n", recurse(6));
    printf("addressed %d\n", addressed());
    printf("inlined %d\n", inlined(1000));
    printf("jumped %d\n", jumped());
    printf("large %d\n", large(3));
    struct record record = { { 1, 2, 3, 4, 5 } };
    printf("copies %ld %ld\n", copies(record, 7), record.values[0]);
    int total = 0;
    for (int index = -2; index < 3; index++) total += checked(index);
    printf("checked %d\n", total);
    return 0;
}
