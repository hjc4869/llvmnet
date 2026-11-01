/* musttail calls run in constant stack: each chain below is far deeper than the 8 MB stack holds as ordinary calls.
 * The shapes: a direct mutual recursion, an indirect chain through a function pointer with float arguments (Skia's
 * raster pipeline stages), and callers whose frame has an alloca, a variadic call or a setjmp, which llvmnet keeps in
 * its alloca arena so that the tail call can still release the frame. */
#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define DEPTH 3000000L
#define MUSTTAIL __attribute__((musttail))

/* At -O2 LLVM would inline a direct mutual recursion and turn a self-recursive one into a loop: the functions below are
 * kept apart by noinline, or call themselves through a volatile pointer. */
__attribute__((noinline)) static long odd(long n, long acc);
__attribute__((noinline)) static long even(long n, long acc) {
    if (n == 0) return acc;
    MUSTTAIL return odd(n - 1, acc + 1);
}
__attribute__((noinline)) static long odd(long n, long acc) {
    if (n == 0) return -acc;
    MUSTTAIL return even(n - 1, acc + 2);
}

typedef float (*stage_fn)(void *program, long n, float r, float g, float b, float a);
static float stage(void *program, long n, float r, float g, float b, float a) {
    if (n == 0) return r + 2 * g + 3 * b + 4 * a;
    stage_fn next = *(stage_fn volatile *)program;
    MUSTTAIL return next(program, n - 1, g, b, a, r + 1);
}

/* The local array escapes into a call the optimizer cannot see through, so it stays an alloca. */
__attribute__((noinline)) static unsigned mix(unsigned *values, int count) {
    unsigned hash = 2166136261u;
    for (int i = 0; i < count; i++) hash = (hash ^ values[i]) * 16777619u;
    return hash;
}
static unsigned with_alloca(long n, unsigned acc);
static unsigned (*volatile next_alloca)(long, unsigned) = with_alloca;
static unsigned with_alloca(long n, unsigned acc) {
    unsigned values[8];
    for (int i = 0; i < 8; i++) values[i] = acc + (unsigned)(i * n);
    if (n == 0) return mix(values, 8);
    MUSTTAIL return next_alloca(n - 1, mix(values, 8));
}

static char line[64];
static long with_varargs(long n, long acc);
static long (*volatile next_varargs)(long, long) = with_varargs;
static long with_varargs(long n, long acc) {
    if (n == 0) return acc;
    int length = snprintf(line, sizeof line, "%ld:%ld", n, acc % 997);
    MUSTTAIL return next_varargs(n - 1, acc + length);
}

static jmp_buf escape;
static long with_setjmp(long n, long acc);
static long (*volatile next_setjmp)(long, long) = with_setjmp;
static long with_setjmp(long n, long acc) {
    jmp_buf local;
    if (setjmp(local) != 0) return -1;
    if (n == 0) longjmp(escape, (int)(acc % 1000) + 1);
    MUSTTAIL return next_setjmp(n - 1, acc + (n & 7));
}

int main(void) {
    stage_fn first = stage;
    printf("mutual: %ld %ld\n", even(DEPTH, 0), even(DEPTH + 1, 0));
    printf("indirect: %.1f\n", (double)stage(&first, DEPTH, 1, 2, 3, 4));
    printf("alloca: %u\n", with_alloca(DEPTH, 7));
    printf("varargs: %ld %s\n", with_varargs(DEPTH / 4, 0), line);
    int jumped = setjmp(escape);
    if (jumped == 0) with_setjmp(DEPTH, 0);
    printf("setjmp: %d\n", jumped);
    return 0;
}
