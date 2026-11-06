/* Gathers of evenly strided elements (SV-40), which the typed lowering reads as wide loads and a shuffle: the loop
 * vectorizer's scalarized interleaved groups (x264's lowres filter, an RGB to gray conversion, strides 2 to 4 of bytes
 * and 16-bit values) and vectors built from strided elements in Clang's vector extensions, at strides 2 to 4 of 1- to
 * 8-byte lanes. Every source lies against a page that is not mapped, before or after it (PROT_NONE where the host
 * protects memory: natively and in system mode), so a gather that read a byte past its last lane or before its first
 * one would fault. The vector forms read bytes between and around the lanes only where other accesses of the block
 * prove them dereferenceable (pairs, odd_even) or the object's size does (local, global). The vectorizer's own
 * interleaved pairs of 16- and 32-bit and float lanes are split into even and odd lanes. Results are printed as
 * checksums; native clang with the same flags is the oracle. */
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

typedef uint8_t u8x8 __attribute__((vector_size(8)));
typedef uint8_t u8x16 __attribute__((vector_size(16)));
typedef uint16_t u16x4 __attribute__((vector_size(8)));
typedef uint16_t u16x8 __attribute__((vector_size(16)));
typedef uint32_t u32x4 __attribute__((vector_size(16)));
typedef float f32x4 __attribute__((vector_size(16)));
typedef double f64x4 __attribute__((vector_size(32)));

static uint64_t h = 14695981039346656037ull;
static void mix(const void *data, size_t size)
{
    for (size_t i = 0; i < size; i++) h = (h ^ ((const uint8_t *)data)[i]) * 1099511628211ull;
}

/* The loop vectorizer's shapes. */
#define FILTER(a, b, c, d) ((((a + b + 1) >> 1) + ((c + d + 1) >> 1) + 1) >> 1)
__attribute__((noinline)) static void lowres(const uint8_t *src0, uint8_t *dst0, uint8_t *dsth, uint8_t *dstv, uint8_t *dstc, intptr_t stride, int width)
{
    const uint8_t *src1 = src0 + stride, *src2 = src1 + stride;
    for (int x = 0; x < width; x++) {
        dst0[x] = FILTER(src0[2 * x], src1[2 * x], src0[2 * x + 1], src1[2 * x + 1]);
        dsth[x] = FILTER(src0[2 * x + 1], src1[2 * x + 1], src0[2 * x + 2], src1[2 * x + 2]);
        dstv[x] = FILTER(src1[2 * x], src2[2 * x], src1[2 * x + 1], src2[2 * x + 1]);
        dstc[x] = FILTER(src1[2 * x + 1], src2[2 * x + 1], src1[2 * x + 2], src2[2 * x + 2]);
    }
}
__attribute__((noinline)) static void gray(const uint8_t *s, uint8_t *d, int n) { for (int i = 0; i < n; i++) d[i] = (uint8_t)((s[3 * i] * 77 + s[3 * i + 1] * 150 + s[3 * i + 2] * 29) >> 8); }
__attribute__((noinline)) static void even8(const uint8_t *s, uint8_t *d, int n) { for (int i = 0; i < n; i++) d[i] = (uint8_t)(s[2 * i] + 1); }
__attribute__((noinline)) static void third8(const uint8_t *s, uint8_t *d, int n) { for (int i = 0; i < n; i++) d[i] = (uint8_t)(s[3 * i] * 3); }
__attribute__((noinline)) static void fourth8(const uint8_t *s, uint8_t *d, int n) { for (int i = 0; i < n; i++) d[i] = (uint8_t)(s[4 * i + 1] ^ 5); }
__attribute__((noinline)) static void even16(const uint16_t *s, uint16_t *d, int n) { for (int i = 0; i < n; i++) d[i] = (uint16_t)(s[2 * i + 1] + 1); }
__attribute__((noinline)) static void third16(const uint16_t *s, uint16_t *d, int n) { for (int i = 0; i < n; i++) d[i] = (uint16_t)(s[3 * i] * 3); }
__attribute__((noinline)) static void fourth16(const uint16_t *s, uint16_t *d, int n) { for (int i = 0; i < n; i++) d[i] = (uint16_t)(s[4 * i + 2] ^ 5); }
/* Pairs the vectorizer loads whole and splits into even and odd lanes of two fragments. */
__attribute__((noinline)) static void pairs16(const int16_t *s, int16_t *d, int n) { for (int i = 0; i < n; i++) d[i] = (int16_t)(s[2 * i] - s[2 * i + 1]); }
__attribute__((noinline)) static void pairs32(const uint32_t *s, uint32_t *d, int n) { for (int i = 0; i < n; i++) d[i] = s[2 * i] - 3 * s[2 * i + 1]; }
__attribute__((noinline)) static void pairsf(const float *s, float *d, int n) { for (int i = 0; i < n; i++) d[i] = s[2 * i] * s[2 * i + 1]; }

/* Vectors of strided elements, the last lane the last element of the span. */
__attribute__((noinline)) static u8x8 b2x8(const uint8_t *s) { return (u8x8){s[0], s[2], s[4], s[6], s[8], s[10], s[12], s[14]}; }
__attribute__((noinline)) static u8x8 b3x8(const uint8_t *s) { return (u8x8){s[0], s[3], s[6], s[9], s[12], s[15], s[18], s[21]}; }
__attribute__((noinline)) static u8x8 b4x8(const uint8_t *s) { return (u8x8){s[0], s[4], s[8], s[12], s[16], s[20], s[24], s[28]}; }
__attribute__((noinline)) static u8x16 b2x16(const uint8_t *s)
{
    return (u8x16){s[0], s[2], s[4], s[6], s[8], s[10], s[12], s[14], s[16], s[18], s[20], s[22], s[24], s[26], s[28], s[30]};
}
__attribute__((noinline)) static u8x16 b3x16(const uint8_t *s)
{
    return (u8x16){s[0], s[3], s[6], s[9], s[12], s[15], s[18], s[21], s[24], s[27], s[30], s[33], s[36], s[39], s[42], s[45]};
}
__attribute__((noinline)) static u8x16 b4x16(const uint8_t *s)
{
    return (u8x16){s[0], s[4], s[8], s[12], s[16], s[20], s[24], s[28], s[32], s[36], s[40], s[44], s[48], s[52], s[56], s[60]};
}
__attribute__((noinline)) static u16x4 w2x4(const uint16_t *s) { return (u16x4){s[0], s[2], s[4], s[6]}; }
__attribute__((noinline)) static u16x4 w3x4(const uint16_t *s) { return (u16x4){s[0], s[3], s[6], s[9]}; }
__attribute__((noinline)) static u16x4 w4x4(const uint16_t *s) { return (u16x4){s[0], s[4], s[8], s[12]}; }
__attribute__((noinline)) static u16x8 w2x8(const uint16_t *s) { return (u16x8){s[0], s[2], s[4], s[6], s[8], s[10], s[12], s[14]}; }
__attribute__((noinline)) static u16x8 w3x8(const uint16_t *s) { return (u16x8){s[0], s[3], s[6], s[9], s[12], s[15], s[18], s[21]}; }
__attribute__((noinline)) static u16x8 w4x8(const uint16_t *s) { return (u16x8){s[0], s[4], s[8], s[12], s[16], s[20], s[24], s[28]}; }
__attribute__((noinline)) static u32x4 d2x4(const uint32_t *s) { return (u32x4){s[0], s[2], s[4], s[6]}; }
__attribute__((noinline)) static u32x4 d3x4(const uint32_t *s) { return (u32x4){s[0], s[3], s[6], s[9]}; }
__attribute__((noinline)) static f32x4 f2x4(const float *s) { return (f32x4){s[0], s[2], s[4], s[6]}; }
__attribute__((noinline)) static f64x4 q2x4(const double *s) { return (f64x4){s[0], s[2], s[4], s[6]}; }
/* Even and odd lanes of one range: each gather's window may read the other's bytes. */
__attribute__((noinline)) static u8x8 pairs(const uint8_t *s)
{
    return (u8x8){s[0], s[2], s[4], s[6], s[8], s[10], s[12], s[14]} + (u8x8){s[1], s[3], s[5], s[7], s[9], s[11], s[13], s[15]};
}
/* Odd lanes and the even lanes after them: bytes 1 to 16, nothing at 0 or 17. */
__attribute__((noinline)) static u8x8 odd_even(const uint8_t *s)
{
    return (u8x8){s[1], s[3], s[5], s[7], s[9], s[11], s[13], s[15]} ^ (u8x8){s[2], s[4], s[6], s[8], s[10], s[12], s[14], s[16]};
}
/* Objects of known size, which may be read whole. */
static uint8_t table[40];
__attribute__((noinline)) static u8x8 global(void) { return (u8x8){table[1], table[4], table[7], table[10], table[13], table[16], table[19], table[22]}; }
__attribute__((noinline)) static void fill(uint8_t *bytes, int count, uint8_t seed) { for (int i = 0; i < count; i++) bytes[i] = (uint8_t)(seed + i * 7); }
__attribute__((noinline)) static u8x8 local(uint8_t seed)
{
    uint8_t bytes[24];
    fill(bytes, 24, seed);
    return (u8x8){bytes[1], bytes[3], bytes[5], bytes[7], bytes[9], bytes[11], bytes[13], bytes[15]};
}

int main(void)
{
    long page = sysconf(_SC_PAGESIZE);
    /* A mapped page between two that are not. */
    uint8_t *mapping = mmap(0, 3 * page, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (mapping == MAP_FAILED) return 1;
    mprotect(mapping, page, PROT_NONE);
    mprotect(mapping + 2 * page, page, PROT_NONE);
    uint8_t *start = mapping + page, *end = start + page;
    for (long i = 0; i < page; i++) start[i] = (uint8_t)(i * 37 + (i >> 5) * 11 + 3);
    for (int i = 0; i < 40; i++) table[i] = (uint8_t)(i * i + 1);

    /* Each kernel's source against the page's end and against its start. */
    static uint8_t out[4][512];
    for (int width = 0; width <= 160; width += width < 24 ? 1 : 8) {
        const uint8_t *src0 = end - (2 * width + 1) - 2 * (2 * width + 1);
        lowres(src0, out[0], out[1], out[2], out[3], 2 * width + 1, width);
        mix(out, sizeof out);
        lowres(start, out[0], out[1], out[2], out[3], 2 * width + 1, width);
        mix(out, sizeof out);
    }
    /* The sources of n elements end at the page's last byte; those of no elements are the page's end. */
#define LAST(span) (end - (n != 0 ? (span) : 0))
    for (int n = 0; n <= 100; n += n < 40 ? 1 : 15) {
        gray(LAST(3 * n), out[0], n);
        gray(start, out[1], n);
        even8(LAST(2 * n - 1), out[2], n);
        even8(start, out[3], n);
        mix(out, sizeof out);
        third8(LAST(3 * n - 2), out[0], n);
        third8(start, out[1], n);
        fourth8(LAST(4 * n - 2), out[2], n);
        fourth8(start - 1, out[3], n);
        mix(out, sizeof out);
        even16((const uint16_t *)LAST(4 * n), (uint16_t *)out[0], n);
        even16((const uint16_t *)start - 1, (uint16_t *)out[1], n);
        third16((const uint16_t *)LAST(6 * n - 4), (uint16_t *)out[2], n);
        third16((const uint16_t *)start, (uint16_t *)out[3], n);
        mix(out, sizeof out);
        fourth16((const uint16_t *)LAST(8 * n - 2), (uint16_t *)out[0], n);
        fourth16((const uint16_t *)start - 2, (uint16_t *)out[1], n);
        pairs16((const int16_t *)LAST(4 * n), (int16_t *)out[2], n);
        mix(out, 3 * sizeof out[0]);
        pairs32((const uint32_t *)LAST(8 * n), (uint32_t *)out[0], n);
        pairsf((const float *)LAST(8 * n), (float *)out[1], n);
        mix(out, 2 * sizeof out[0]);
    }
#define BOTH(gather, type, span)                                    \
    do {                                                            \
        type value = gather((const void *)(end - (span)));         \
        mix(&value, sizeof value);                                  \
        value = gather((const void *)start);                        \
        mix(&value, sizeof value);                                  \
    } while (0)
    BOTH(b2x8, u8x8, 15);
    BOTH(b3x8, u8x8, 22);
    BOTH(b4x8, u8x8, 29);
    BOTH(b2x16, u8x16, 31);
    BOTH(b3x16, u8x16, 46);
    BOTH(b4x16, u8x16, 61);
    BOTH(w2x4, u16x4, 14);
    BOTH(w3x4, u16x4, 20);
    BOTH(w4x4, u16x4, 26);
    BOTH(w2x8, u16x8, 30);
    BOTH(w3x8, u16x8, 44);
    BOTH(w4x8, u16x8, 58);
    BOTH(d2x4, u32x4, 28);
    BOTH(d3x4, u32x4, 40);
    BOTH(f2x4, f32x4, 28);
    BOTH(q2x4, f64x4, 56);
    BOTH(pairs, u8x8, 16);
    u8x8 value = odd_even(end - 17);
    mix(&value, sizeof value);
    value = odd_even(start - 1);
    mix(&value, sizeof value);
    value = global();
    mix(&value, sizeof value);
    value = local(9);
    mix(&value, sizeof value);
    printf("checksum %016llx\n", (unsigned long long)h);
    return 0;
}
