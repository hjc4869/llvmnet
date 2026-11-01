/* Branch-free select, abs and min/max: every pair of edge values of every integer width, each comparison predicate as
 * the condition of a select of a third and fourth value, and floating-point selects, which keep their branch. At -O2
 * Clang turns these ternaries into select, llvm.smin/smax/umin/umax and llvm.abs (tests/select.sh checks that). The
 * output is a hash per group, compared with the native build's. The tables are read through a volatile pointer, so
 * nothing folds to a constant. */
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#define COUNT 16
static const volatile int64_t source[COUNT] = {
    0, 1, -1, 2, -2, 127, -128, 255, 32767, -32768, 65535, INT32_MAX, INT32_MIN, UINT32_MAX, INT64_MAX, INT64_MIN,
};
static int64_t values[COUNT];
static uint64_t hash = 14695981039346656037u;
static void mix(uint64_t value) { hash = (hash ^ value) * 1099511628211u; }

#define MINMAX(T, NAME)                                                                                                   \
    static void NAME(void) {                                                                                              \
        for (int i = 0; i < COUNT; i++)                                                                                   \
            for (int j = 0; j < COUNT; j++) {                                                                             \
                T x = (T)values[i], y = (T)values[j], u = (T)values[(i + j) % COUNT], v = (T)values[(i * j + 3) % COUNT]; \
                mix((uint64_t)(x < y ? x : y));                                                                           \
                mix((uint64_t)(x > y ? x : y));                                                                           \
                mix((uint64_t)(x <= y ? y : x));                                                                          \
                mix((uint64_t)(x >= y ? y : x));                                                                          \
                mix((uint64_t)(x < y ? u : v));                                                                           \
                mix((uint64_t)(x > y ? u : v));                                                                           \
                mix((uint64_t)(x <= y ? u : v));                                                                          \
                mix((uint64_t)(x >= y ? u : v));                                                                          \
                mix((uint64_t)(x == y ? u : v));                                                                          \
                mix((uint64_t)(x != y ? u : v));                                                                          \
                mix((uint64_t)(x < y ? u : 0));                                                                           \
                mix((uint64_t)(x < y ? 0 : v));                                                                           \
                mix((uint64_t)(x < 0 ? (T)(0 - (uint64_t)x) : x));                                                        \
                mix((uint64_t)(x < y && u < v));                                                                          \
                mix((uint64_t)(x < y || u < v));                                                                          \
            }                                                                                                             \
        printf("%-20s %016llx\n", #T, (unsigned long long)hash);                                                          \
    }

MINMAX(signed char, s8)
MINMAX(unsigned char, u8)
MINMAX(short, s16)
MINMAX(unsigned short, u16)
MINMAX(int, s32)
MINMAX(unsigned, u32)
MINMAX(long, s64)
MINMAX(unsigned long, u64)
MINMAX(_BitInt(33), s33)
MINMAX(unsigned _BitInt(33), u33)
MINMAX(_BitInt(17), s17)

/* The library functions are undefined for the minimum value, which the table holds; it is skipped. */
static void library(void) {
    for (int i = 0; i < COUNT; i++) {
        if ((int)values[i] != INT32_MIN) mix((uint64_t)abs((int)values[i]));
        if (values[i] != INT64_MIN) mix((uint64_t)labs((long)values[i]));
        if (values[i] != INT64_MIN) mix((uint64_t)llabs((long long)values[i]));
    }
    printf("%-20s %016llx\n", "abs/labs/llabs", (unsigned long long)hash);
}

/* A select between pointers, and one whose condition is a loaded flag rather than a comparison. */
static char cells[COUNT];
static void pointers(void) {
    for (int i = 0; i < COUNT; i++)
        for (int j = 0; j < COUNT; j++) {
            char *p = &cells[i], *q = &cells[j];
            mix((uint64_t)((values[i] < values[j] ? p : q) - cells));
            mix((uint64_t)((p < q ? p : q) - cells));
            _Bool flag = values[(i + j) % COUNT] & 1;
            mix((uint64_t)(flag ? values[i] : values[j]));
            mix((uint64_t)(flag ? (int)values[i] : (int)values[j]));
        }
    printf("%-20s %016llx\n", "pointers/flags", (unsigned long long)hash);
}

/* Floating-point selects keep their branch, so NaN, signed zeros and infinities pass through exactly. */
static void floating(void) {
    double table[] = {0.0, -0.0, 1.5, -1.5, INFINITY, -INFINITY, NAN, 1e-310};
    for (int i = 0; i < 8; i++)
        for (int j = 0; j < 8; j++) {
            double x = table[i] * (double)(values[1] != 0), y = table[j] * (double)(values[1] != 0);
            float f = (float)x, g = (float)y;
            union { double d; uint64_t u; } a = {x < y ? x : y}, b = {x > y ? x : y}, c = {values[i] < values[j] ? x : y};
            union { float f; uint32_t u; } d = {f < g ? f : g}, e = {fabsf(f) < fabsf(g) ? g : f};
            mix(a.u), mix(b.u), mix(c.u), mix(d.u), mix(e.u);
        }
    printf("%-20s %016llx\n", "floating", (unsigned long long)hash);
}

/* Kernels whose CIL tests/select.sh checks for branches: libpng's Paeth predictor (three abs, an unsigned minimum and
 * two selects), a 64-bit clamp (smax and smin), a select on a flag argument, and a select of 64-bit values on a 64-bit
 * comparison. */
__attribute__((noinline)) static int paeth_predictor(int a, int b, int c) {
    int p = b - c, pc = a - c;
    int pa = p < 0 ? -p : p, pb = pc < 0 ? -pc : pc;
    pc = (p + pc) < 0 ? -(p + pc) : p + pc;
    if (pb < pa) { pa = pb; a = b; }
    if (pc < pa) a = c;
    return a;
}
__attribute__((noinline)) static long clamp_long(long x, long low, long high) { return x < low ? low : x > high ? high : x; }
__attribute__((noinline)) static unsigned select_flag(_Bool flag, unsigned a, unsigned b) { return flag ? a : b; }
__attribute__((noinline)) static long select_wide(long x, long y, long a, long b) { return x < y ? a : b; }

static void kernels(void) {
    for (int i = 0; i < COUNT; i++)
        for (int j = 0; j < COUNT; j++) {
            int64_t x = values[i], y = values[j], z = values[(i + j) % COUNT];
            mix((uint64_t)paeth_predictor((int)(x & 255), (int)(y & 255), (int)(z & 255)));
            mix((uint64_t)clamp_long(x, y < z ? y : z, y < z ? z : y));
            mix(select_flag(z & 1, (unsigned)x, (unsigned)y));
            mix((uint64_t)select_wide(x, y, z, x ^ y));
        }
    printf("%-20s %016llx\n", "kernels", (unsigned long long)hash);
}

int main(void) {
    for (int i = 0; i < COUNT; i++) values[i] = source[i];
    s8(), u8(), s16(), u16(), s32(), u32(), s64(), u64(), s33(), u33(), s17();
    library();
    pointers();
    floating();
    kernels();
    return 0;
}
