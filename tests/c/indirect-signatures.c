/* Indirect calls of many signatures (PF-27): NativeAOT calls these through inlined RawCalliHelper stubs, one per
 * signature, including the ones whose CIL signature the compiler patches in after emission (aggregates, vectors,
 * long double, __int128), calls with arguments on the stack and calls that libc makes back into the program. The
 * program prints the same natively and translated, under the JIT and as NativeAOT. */
#include <stdio.h>
#include <stdlib.h>

struct pair { long integer; double real; };
struct big { long values[5]; };
typedef int lanes __attribute__((vector_size(16)));

__attribute__((noinline)) static void nothing(void) { puts("nothing"); }
__attribute__((noinline)) static int add(int a, int b) { return a + b; }
__attribute__((noinline)) static int subtract(int a, int b) { return a - b; }
__attribute__((noinline)) static struct pair make(long a, double b) { struct pair p = { a * 2, b / 2 }; return p; }
__attribute__((noinline)) static struct big fill(long base) { struct big b; for (int i = 0; i < 5; i++) b.values[i] = base + i; return b; }
__attribute__((noinline)) static lanes twice(lanes v) { return v + v; }
__attribute__((noinline)) static long double third(long double v) { return v / 3; }
__attribute__((noinline)) static __int128 shifted(__int128 v, int by) { return v << by; }
__attribute__((noinline)) static long nine(long a, long b, long c, long d, long e, long f, long g, long h, long i)
{
    return a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f + 7 * g + 8 * h + 9 * i;
}
__attribute__((noinline)) static const char *name(int which) { return which ? "second" : "first"; }
static int compare(const void *left, const void *right) { return *(const int *)left - *(const int *)right; }

/* volatile keeps every call indirect at -O2. */
static void (*volatile p_nothing)(void) = nothing;
static int (*volatile p_operations[2])(int, int) = { add, subtract };
static struct pair (*volatile p_make)(long, double) = make;
static struct big (*volatile p_fill)(long) = fill;
static lanes (*volatile p_twice)(lanes) = twice;
static long double (*volatile p_third)(long double) = third;
static __int128 (*volatile p_shifted)(__int128, int) = shifted;
static long (*volatile p_nine)(long, long, long, long, long, long, long, long, long) = nine;
static const char *(*volatile p_name)(int) = name;

int main(void)
{
    p_nothing();
    long total = 0;
    for (int i = 0; i < 1000; i++) total += p_operations[i & 1](i, 3);
    struct pair p = p_make(21, 5.0);
    struct big b = p_fill(10);
    lanes v = p_twice((lanes){ 1, 2, 3, 4 });
    __int128 wide = p_shifted(3, 100);
    printf("%ld %ld %g %ld %ld %d %d %d %d %.6Lf %llx%016llx\n", total, p.integer, p.real, b.values[0], b.values[4],
           v[0], v[1], v[2], v[3], p_third(1.0L), (unsigned long long)(wide >> 64), (unsigned long long)wide);
    printf("%ld %s %s\n", p_nine(1, 2, 3, 4, 5, 6, 7, 8, 9), p_name(0), p_name(1));
    int values[] = { 5, 3, 9, 1, 7 };
    qsort(values, 5, sizeof values[0], compare);
    printf("%d %d %d %d %d\n", values[0], values[1], values[2], values[3], values[4]);
    return 0;
}
