/* SIMD kernels of the SIMD survey (docs/evidence/2026-11-03-simd-survey.md): N = 4096 element static arrays, each kernel
 * repeated R_<kernel> times, so that the native scalar build runs 0.3 to 0.7 s. The benchmark set's simd group runs them
 * vectorized at each ISA level (SV-20); compiled with -ffp-contract=off, every build of a kernel prints the same line.
 * usage: simd-kernels <saxpy|i32mix|sad8|dot16|max8|fdot|fdot8|paeth|none>
 * Prints "<kernel> checksum=<hex>" (plus a human-readable value for the float kernels). */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define N 4096
#ifndef R_SAXPY
#define R_SAXPY 700000
#endif
#ifndef R_I32MIX
#define R_I32MIX 500000
#endif
#ifndef R_SAD8
#define R_SAD8 500000
#endif
#ifndef R_DOT16
#define R_DOT16 600000
#endif
#ifndef R_MAX8
#define R_MAX8 600000
#endif
#ifndef R_FDOT
#define R_FDOT 800000
#endif
#ifndef R_FDOT8
#define R_FDOT8 800000
#endif
#ifndef R_PAETH
#define R_PAETH 120000
#endif

#define NOINLINE __attribute__((noinline))

static float sx[N], sy[N];          /* saxpy */
static int32_t ia[N], ib[N], ic[N]; /* i32mix */
static uint8_t ua[N], ub[N];        /* sad8 */
static int16_t ha[N], hb[N];        /* dot16 */
static uint8_t ma[N];               /* max8 */
static float dx[N], dy[N];          /* fdot, fdot8 */
static uint8_t prow[N], pprev[N];   /* paeth */

static uint32_t hash32(uint32_t i)
{
    uint32_t h = i * 2654435761u + 0x9e3779b9u;
    h ^= h >> 15;
    h *= 0x85ebca6bu;
    h ^= h >> 13;
    h *= 0xc2b2ae35u;
    h ^= h >> 16;
    return h;
}

/* unit float in [-1, 1) from the low 16 bits of h */
static float unitf(uint32_t h) { return (float)(int32_t)(h & 0xffffu) * (1.0f / 32768.0f) - 1.0f; }

static uint32_t fbits(float f)
{
    uint32_t u;
    memcpy(&u, &f, sizeof u);
    return u;
}

NOINLINE static void fill(void)
{
    for (int i = 0; i < N; i++) {
        uint32_t h = hash32((uint32_t)i), g = hash32((uint32_t)i + 0x10000u);
        sx[i] = unitf(h);
        sy[i] = unitf(g);
        ia[i] = (int32_t)h;
        ib[i] = (int32_t)g;
        ic[i] = 0;
        ua[i] = (uint8_t)h;
        ub[i] = (uint8_t)g;
        ha[i] = (int16_t)h;
        hb[i] = (int16_t)g;
        ma[i] = (uint8_t)(h % 251u);
        dx[i] = unitf(h >> 8);
        dy[i] = unitf(g >> 8);
        prow[i] = (uint8_t)(h >> 16);
        pprev[i] = (uint8_t)(g >> 16);
    }
}

/* a. float y[i] = a*x[i] + y[i] */
NOINLINE static uint32_t k_saxpy(int rounds)
{
    const float a = 0.001f;
    for (int r = 0; r < rounds; r++)
        for (int i = 0; i < N; i++)
            sy[i] = a * sx[i] + sy[i];
    uint32_t cs = 0;
    for (int i = 0; i < N; i++)
        cs = cs * 31u + fbits(sy[i]);
    return cs;
}

/* b. int32 c[i] = a[i]*3 + (b[i]>>2) - (a[i]&b[i]) */
NOINLINE static uint32_t k_i32mix(int rounds)
{
    uint32_t cs = 0;
    for (int r = 0; r < rounds; r++) {
        ia[r & (N - 1)] += r;
        for (int i = 0; i < N; i++)
            ic[i] = ia[i] * 3 + (ib[i] >> 2) - (ia[i] & ib[i]);
        cs += (uint32_t)ic[r & (N - 1)];
    }
    for (int i = 0; i < N; i++)
        cs = cs * 31u + (uint32_t)ic[i];
    return cs;
}

/* c. sum |a[i]-b[i]| over uint8 arrays, accumulated in uint32 */
NOINLINE static uint32_t k_sad8(int rounds)
{
    uint32_t cs = 0;
    for (int r = 0; r < rounds; r++) {
        ua[r & (N - 1)] = (uint8_t)(r * 7);
        uint32_t s = 0;
        for (int i = 0; i < N; i++) {
            int d = (int)ua[i] - (int)ub[i];
            s += (uint32_t)(d < 0 ? -d : d);
        }
        cs = cs * 31u + s;
    }
    return cs;
}

/* d. int32 acc += (int32)a[i]*b[i] over int16 arrays */
NOINLINE static uint32_t k_dot16(int rounds)
{
    uint32_t cs = 0;
    for (int r = 0; r < rounds; r++) {
        ha[r & (N - 1)] = (int16_t)(r * 13);
        int32_t s = 0;
        for (int i = 0; i < N; i++)
            s += (int32_t)ha[i] * (int32_t)hb[i];
        cs = cs * 31u + (uint32_t)s;
    }
    return cs;
}

/* e. maximum over a uint8 array */
NOINLINE static uint32_t k_max8(int rounds)
{
    uint32_t cs = 0;
    for (int r = 0; r < rounds; r++) {
        ma[r & (N - 1)] = (uint8_t)((uint32_t)r * 7u % 251u);
        uint8_t m = 0;
        for (int i = 0; i < N; i++)
            m = ma[i] > m ? ma[i] : m;
        cs = cs * 31u + m;
    }
    return cs;
}

/* f. float dot product with 4 independent partial sums (no fast-math) */
NOINLINE static uint32_t k_fdot(int rounds, float *out)
{
    uint32_t cs = 0;
    float last = 0.0f;
    for (int r = 0; r < rounds; r++) {
        dx[r & (N - 1)] = unitf((uint32_t)r * 2654435761u);
        float s0 = 0.0f, s1 = 0.0f, s2 = 0.0f, s3 = 0.0f;
        for (int i = 0; i < N; i += 4) {
            s0 += dx[i + 0] * dy[i + 0];
            s1 += dx[i + 1] * dy[i + 1];
            s2 += dx[i + 2] * dy[i + 2];
            s3 += dx[i + 3] * dy[i + 3];
        }
        last = (s0 + s1) + (s2 + s3);
        cs = cs * 31u + fbits(last);
    }
    *out = last;
    return cs;
}

/* f'. the same with 8 independent partial sums (what a 256-bit lowering needs) */
NOINLINE static uint32_t k_fdot8(int rounds, float *out)
{
    uint32_t cs = 0;
    float last = 0.0f;
    for (int r = 0; r < rounds; r++) {
        dx[r & (N - 1)] = unitf((uint32_t)r * 2654435761u);
        float s0 = 0.0f, s1 = 0.0f, s2 = 0.0f, s3 = 0.0f, s4 = 0.0f, s5 = 0.0f, s6 = 0.0f, s7 = 0.0f;
        for (int i = 0; i < N; i += 8) {
            s0 += dx[i + 0] * dy[i + 0];
            s1 += dx[i + 1] * dy[i + 1];
            s2 += dx[i + 2] * dy[i + 2];
            s3 += dx[i + 3] * dy[i + 3];
            s4 += dx[i + 4] * dy[i + 4];
            s5 += dx[i + 5] * dy[i + 5];
            s6 += dx[i + 6] * dy[i + 6];
            s7 += dx[i + 7] * dy[i + 7];
        }
        last = ((s0 + s1) + (s2 + s3)) + ((s4 + s5) + (s6 + s7));
        cs = cs * 31u + fbits(last);
    }
    *out = last;
    return cs;
}

/* g. libpng Paeth filter row (png_read_filter_row_paeth_multibyte_pixel), bpp = 4, in place; the control */
NOINLINE static uint32_t k_paeth(int rounds)
{
    const unsigned bpp = 4;
    for (int r = 0; r < rounds; r++) {
        uint8_t *row = prow;
        const uint8_t *prev = pprev;
        unsigned i;
        for (i = 0; i < bpp; i++) {
            int a = *row + *prev++;
            *row++ = (uint8_t)a;
        }
        for (; i < N; i++) {
            int a, b, c, pa, pb, pc, p;
            a = row[-(int)bpp];
            c = prev[-(int)bpp];
            b = *prev++;
            p = b - c;
            pc = a - c;
            pa = abs(p);
            pb = abs(pc);
            pc = abs(p + pc);
            if (pb < pa) {
                pa = pb;
                a = b;
            }
            if (pc < pa)
                a = c;
            a += *row;
            *row++ = (uint8_t)a;
        }
    }
    uint32_t cs = 0;
    for (int i = 0; i < N; i++)
        cs = cs * 31u + prow[i];
    return cs;
}

int main(int argc, char **argv)
{
    const char *k = argc > 1 ? argv[1] : "none";
    fill();
    uint32_t cs = 0;
    float f = 0.0f;
    if (!strcmp(k, "saxpy")) cs = k_saxpy(R_SAXPY);
    else if (!strcmp(k, "i32mix")) cs = k_i32mix(R_I32MIX);
    else if (!strcmp(k, "sad8")) cs = k_sad8(R_SAD8);
    else if (!strcmp(k, "dot16")) cs = k_dot16(R_DOT16);
    else if (!strcmp(k, "max8")) cs = k_max8(R_MAX8);
    else if (!strcmp(k, "fdot")) cs = k_fdot(R_FDOT, &f);
    else if (!strcmp(k, "fdot8")) cs = k_fdot8(R_FDOT8, &f);
    else if (!strcmp(k, "paeth")) cs = k_paeth(R_PAETH);
    else if (strcmp(k, "none")) {
        fprintf(stderr, "unknown kernel %s\n", k);
        return 2;
    }
    if (!strcmp(k, "fdot") || !strcmp(k, "fdot8"))
        printf("%s checksum=%08x last=%.9g\n", k, cs, (double)f);
    else
        printf("%s checksum=%08x\n", k, cs);
    return 0;
}
