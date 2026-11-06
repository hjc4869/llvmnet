/* Pixel kernels in the shapes video encoders give the vectorizers, whose ARM64 forms llvmnet chooses apart from x64's
 * (SV-34 to SV-39): rows of 4, 8 and 16 bytes loaded and stored, each row's sum of absolute differences reduced on its
 * own, differences and sums of bytes and halfwords widened to 16 and 32 bits, a quantization that narrows 32-bit
 * products to halfwords, a 4x4 Hadamard transform of differences carried as two 16-bit halves of 32-bit lanes with the
 * sign-mask absolute value of each half, and the constant shuffles of butterflies and transposes. Prints checksums;
 * native clang with the same flags is the oracle. */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#define NOINLINE __attribute__((noinline))
#define STRIDE 40
#define ROWS 24

static uint8_t pa[STRIDE * ROWS], pb[STRIDE * ROWS], out[STRIDE * ROWS];
static int16_t ha[64], hb[64], coefficients[64];
static uint16_t multipliers[64], biases[64];

static uint32_t hash(uint32_t i)
{
    uint32_t h = i * 2654435761u + 0x7f4a7c15u;
    h ^= h >> 16;
    h *= 0x85ebca6bu;
    h ^= h >> 13;
    return h;
}

static uint64_t mix(uint64_t h, uint64_t value) { return (h ^ value) * 1099511628211ull; }

#define SAD(W, H)                                                                                                       \
    NOINLINE static int sad_##W##x##H(const uint8_t *a, int sa, const uint8_t *b, int sb)                                \
    {                                                                                                                   \
        int sum = 0;                                                                                                    \
        for (int y = 0; y < H; y++, a += sa, b += sb)                                                                   \
            for (int x = 0; x < W; x++) sum += abs(a[x] - b[x]);                                                        \
        return sum;                                                                                                     \
    }
SAD(4, 4)
SAD(8, 4)
SAD(8, 8)
SAD(16, 8)

/* Rows of 8 and 4 bytes computed and stored. */
NOINLINE static void average8(uint8_t *restrict d, const uint8_t *a, const uint8_t *b)
{
    for (int y = 0; y < 16; y++, d += STRIDE, a += STRIDE, b += STRIDE)
        for (int x = 0; x < 8; x++) d[x] = (uint8_t)((a[x] + b[x] + 1) >> 1);
}

NOINLINE static void blend4(uint8_t *restrict d, const uint8_t *a, const uint8_t *b)
{
    for (int y = 0; y < 16; y++, d += STRIDE, a += STRIDE, b += STRIDE)
        for (int x = 0; x < 4; x++) d[x] = (uint8_t)((a[x] ^ b[x]) + 3);
}

/* Differences of 8 bytes as halfwords, and their squares summed in 32 bits. */
NOINLINE static void differences(int16_t *restrict d, const uint8_t *a, const uint8_t *b)
{
    for (int y = 0; y < 8; y++, d += 8, a += STRIDE, b += STRIDE)
        for (int x = 0; x < 8; x++) d[x] = (int16_t)(a[x] - b[x]);
}

#define SQUARES(W)                                                                                                      \
    NOINLINE static int squares##W(const uint8_t *a, const uint8_t *b)                                                  \
    {                                                                                                                   \
        int sum = 0;                                                                                                    \
        for (int y = 0; y < 8; y++, a += STRIDE, b += STRIDE)                                                           \
            for (int x = 0; x < W; x++) {                                                                               \
                int d = a[x] - b[x];                                                                                    \
                sum += d * d;                                                                                           \
            }                                                                                                           \
        return sum;                                                                                                     \
    }
SQUARES(4)
SQUARES(8)
SQUARES(16)

/* Sums of bytes and halfwords widened to 32 bits, and halfword and byte differences. */
NOINLINE static void sums(int32_t *restrict d, const uint8_t *a, const uint8_t *b, const int16_t *x, const int16_t *y)
{
    for (int i = 0; i < 4; i++) d[i] = a[i] + b[i];
    for (int i = 0; i < 8; i++) d[4 + i] = x[i] + y[i];
    for (int i = 0; i < 8; i++) d[12 + i] = x[i] - y[i];
    for (int i = 0; i < 16; i++) d[20 + i] = a[16 + i] - b[16 + i];
}

/* Coefficients scaled in 32 bits and narrowed back to halfwords; whether any is nonzero. */
NOINLINE static int quantize(int16_t *c, const uint16_t *mf, const uint16_t *bias)
{
    int any = 0;
    for (int i = 0; i < 16; i++) {
        int v = c[i];
        v = v > 0 ? (int)((uint32_t)(bias[i] + v) * mf[i] >> 16) : -(int)((uint32_t)(bias[i] - v) * mf[i] >> 16);
        c[i] = (int16_t)v;
        any |= v;
    }
    return any != 0;
}

/* The absolute value of each 16-bit half of a 32-bit lane: its sign spread over the half, added and flipped. */
static inline uint32_t absolute_halves(uint32_t v)
{
    uint32_t sign = ((v >> 15) & 0x10001u) * 0xffffu;
    return (v + sign) ^ sign;
}

/* A 4x4 Hadamard transform of an 8x4 block's differences, its left and right halves as the low and high halfwords. */
NOINLINE static int hadamard(const uint8_t *a, int sa, const uint8_t *b, int sb)
{
    uint32_t rows[4][4], total = 0;
    for (int y = 0; y < 4; y++, a += sa, b += sb) {
        uint32_t d[4];
        for (int x = 0; x < 4; x++) d[x] = (uint32_t)(a[x] - b[x]) + ((uint32_t)(a[x + 4] - b[x + 4]) << 16);
        uint32_t s0 = d[0] + d[1], d0 = d[0] - d[1], s1 = d[2] + d[3], d1 = d[2] - d[3];
        rows[y][0] = s0 + s1;
        rows[y][1] = d0 + d1;
        rows[y][2] = s0 - s1;
        rows[y][3] = d0 - d1;
    }
    for (int x = 0; x < 4; x++) {
        uint32_t s0 = rows[0][x] + rows[1][x], d0 = rows[0][x] - rows[1][x], s1 = rows[2][x] + rows[3][x], d1 = rows[2][x] - rows[3][x];
        total += absolute_halves(s0 + s1) + absolute_halves(d0 + d1) + absolute_halves(s0 - s1) + absolute_halves(d0 - d1);
    }
    return (int)(((uint16_t)total + (total >> 16)) >> 1);
}

typedef int32_t i32x4 __attribute__((vector_size(16)));
typedef int16_t i16x8 __attribute__((vector_size(16)));
typedef uint8_t u8x16 __attribute__((vector_size(16)));
typedef float f32x4 __attribute__((vector_size(16)));

/* Constant shuffles of one and two sources: lane reversals, rotations, interleaves, even and odd lanes, transposes,
 * halves, with and without undefined lanes. */
NOINLINE static i32x4 shuffles32(i32x4 a, i32x4 b)
{
    i32x4 r = __builtin_shufflevector(a, a, 1, 0, 3, 2);
    r += __builtin_shufflevector(a, b, 0, 4, 1, 5) * 3;
    r ^= __builtin_shufflevector(a, b, 2, 6, 3, 7);
    r += __builtin_shufflevector(a, b, 0, 2, 4, 6) * 5;
    r -= __builtin_shufflevector(a, b, 1, 3, 5, 7);
    r ^= __builtin_shufflevector(a, b, 0, 4, 2, 6) * 7;
    r += __builtin_shufflevector(a, b, 1, 5, 3, 7);
    r -= __builtin_shufflevector(a, b, 1, 2, 3, 4) * 9;
    r ^= __builtin_shufflevector(a, a, 3, 0, 1, 2);
    r += __builtin_shufflevector(a, b, 0, 1, 6, 7) * 11;
    r += __builtin_shufflevector(a, b, 2, 3, 4, 5);
    r ^= __builtin_shufflevector(b, a, 2, 3, 6, 7) * 13;
    r += __builtin_shufflevector(a, a, 0, 0, 1, 1);
    r -= __builtin_shufflevector(b, b, 2, 2, 3, 3) * 15;
    r += __builtin_shufflevector(a, a, 0, 2, 0, 2);
    r ^= __builtin_shufflevector(b, b, 1, 1, 3, 3) * 17;
    r += __builtin_shufflevector(a, a, 3, 2, 1, 0);
    r += __builtin_shufflevector(b, a, 3, 4, 5, 6);
    return r;
}

NOINLINE static i16x8 shuffles16(i16x8 a, i16x8 b)
{
    i16x8 r = __builtin_shufflevector(a, a, 1, 0, 3, 2, 5, 4, 7, 6);
    r += __builtin_shufflevector(a, a, 3, 2, 1, 0, 7, 6, 5, 4) * 3;
    r ^= __builtin_shufflevector(a, b, 0, 8, 1, 9, 2, 10, 3, 11);
    r += __builtin_shufflevector(a, b, 4, 12, 5, 13, 6, 14, 7, 15) * 5;
    r -= __builtin_shufflevector(a, b, 0, 2, 4, 6, 8, 10, 12, 14);
    r ^= __builtin_shufflevector(a, b, 1, 3, 5, 7, 9, 11, 13, 15) * 7;
    r += __builtin_shufflevector(a, b, 0, 8, 2, 10, 4, 12, 6, 14);
    r -= __builtin_shufflevector(a, b, 1, 9, 3, 11, 5, 13, 7, 15) * 9;
    r ^= __builtin_shufflevector(a, b, 3, 4, 5, 6, 7, 8, 9, 10);
    return r;
}

/* Halves of the lanes, which run in the low half of a fragment whose upper lanes are free. */
typedef int16_t i16x4 __attribute__((vector_size(8)));
typedef int32_t i32x2 __attribute__((vector_size(8)));

NOINLINE static i16x4 halves16(i16x8 a, i16x8 b)
{
    return __builtin_shufflevector(a, b, 0, 2, 4, 6) + __builtin_shufflevector(a, a, 1, 3, 5, 7) * 3 +
        (__builtin_shufflevector(a, b, 0, 8, 1, 9) ^ __builtin_shufflevector(b, b, 1, 0, 3, 2));
}

NOINLINE static i32x2 halves32(i32x4 a, i32x4 b) { return __builtin_shufflevector(a, a, 1, 3) * 5 - __builtin_shufflevector(a, b, 0, 4); }

NOINLINE static u8x16 shuffles8(u8x16 a, u8x16 b)
{
    u8x16 r = __builtin_shufflevector(a, a, 1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);
    r += __builtin_shufflevector(a, a, 3, 2, 1, 0, 7, 6, 5, 4, 11, 10, 9, 8, 15, 14, 13, 12) * 3;
    r ^= __builtin_shufflevector(a, a, 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8);
    r += __builtin_shufflevector(a, b, 0, 16, 1, 17, 2, 18, 3, 19, 4, 20, 5, 21, 6, 22, 7, 23) * 5;
    r -= __builtin_shufflevector(a, b, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31);
    r ^= __builtin_shufflevector(a, b, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20);
    return r;
}

NOINLINE static f32x4 shuffles_float(f32x4 a, f32x4 b)
{
    f32x4 r = __builtin_shufflevector(a, a, 1, 0, 3, 2);
    r += __builtin_shufflevector(a, b, 0, 4, 1, 5) * 3.0f;
    r -= __builtin_shufflevector(a, b, 1, 3, 5, 7);
    r += __builtin_shufflevector(a, b, 2, 3, 4, 5) * 0.5f;
    r -= __builtin_shufflevector(a, b, 0, 1, 6, 7);
    return r;
}

int main(void)
{
    for (int i = 0; i < STRIDE * ROWS; i++) {
        pa[i] = (uint8_t)hash(i);
        pb[i] = (uint8_t)(hash(i + 7919) % 3 == 0 ? pa[i] + (hash(i) % 9) - 4 : hash(i + 104729));
    }
    for (int i = 0; i < 64; i++) {
        ha[i] = (int16_t)hash(3 * i);
        hb[i] = (int16_t)(hash(5 * i) >> 3);
        multipliers[i] = (uint16_t)(hash(11 * i) % 30000 + 1);
        biases[i] = (uint16_t)(hash(13 * i) % 2000);
    }
    uint64_t h = 1469598103934665603ull;
    for (int y = 0; y + 16 <= ROWS; y += 3)
        for (int x = 0; x + 16 <= STRIDE; x += 5) {
            const uint8_t *a = pa + y * STRIDE + x, *b = pb + y * STRIDE + x;
            h = mix(h, (uint32_t)sad_4x4(a, STRIDE, b, STRIDE));
            h = mix(h, (uint32_t)sad_8x4(a, STRIDE, b, STRIDE));
            h = mix(h, (uint32_t)sad_8x8(a, STRIDE, b, STRIDE));
            h = mix(h, (uint32_t)sad_16x8(a, STRIDE, b, STRIDE));
            h = mix(h, (uint32_t)hadamard(a, STRIDE, b, STRIDE));
            h = mix(h, (uint32_t)squares4(a, b));
            h = mix(h, (uint32_t)squares8(a, b));
            h = mix(h, (uint32_t)squares16(a, b));
        }
    printf("sums of absolute differences, transforms, squares: %016llx\n", (unsigned long long)h);
    average8(out, pa, pb);
    blend4(out + 8, pa + 3, pb + 1);
    h = 1469598103934665603ull;
    for (int i = 0; i < STRIDE * ROWS; i++) h = mix(h, out[i]);
    printf("rows stored: %016llx\n", (unsigned long long)h);
    int16_t d[64];
    int32_t s[36];
    h = 1469598103934665603ull;
    for (int k = 0; k < 6; k++) {
        differences(d, pa + 17 * k, pb + 13 * k);
        for (int i = 0; i < 64; i++) h = mix(h, (uint16_t)d[i]);
        sums(s, pa + 9 * k, pb + 4 * k, ha + k, hb + 2 * k);
        for (int i = 0; i < 36; i++) h = mix(h, (uint32_t)s[i]);
    }
    printf("widened differences and sums: %016llx\n", (unsigned long long)h);
    h = 1469598103934665603ull;
    for (int k = 0; k < 4; k++) {
        for (int i = 0; i < 16; i++) coefficients[i] = (int16_t)(ha[i + 4 * k] >> (k * 3));
        h = mix(h, (uint32_t)quantize(coefficients, multipliers + 8 * k, biases + 4 * k));
        for (int i = 0; i < 16; i++) h = mix(h, (uint16_t)coefficients[i]);
    }
    for (int i = 0; i < 16; i++) coefficients[i] = 0;
    h = mix(h, (uint32_t)quantize(coefficients, multipliers, biases));
    printf("quantized: %016llx\n", (unsigned long long)h);
    h = 1469598103934665603ull;
    for (int k = 0; k < 8; k++) {
        i32x4 a32 = {(int32_t)hash(k), (int32_t)hash(k + 1), (int32_t)hash(k + 2), (int32_t)hash(k + 3)};
        i32x4 b32 = {(int32_t)hash(k + 9), (int32_t)hash(k + 10), (int32_t)hash(k + 11), (int32_t)hash(k + 12)};
        i32x4 r32 = shuffles32(a32, b32);
        for (int i = 0; i < 4; i++) h = mix(h, (uint32_t)r32[i]);
        i16x8 a16, b16;
        for (int i = 0; i < 8; i++) a16[i] = (int16_t)hash(20 * k + i), b16[i] = (int16_t)hash(20 * k + i + 8);
        i16x8 r16 = shuffles16(a16, b16);
        for (int i = 0; i < 8; i++) h = mix(h, (uint16_t)r16[i]);
        i16x4 q16 = halves16(a16, b16);
        for (int i = 0; i < 4; i++) h = mix(h, (uint16_t)q16[i]);
        i32x2 q32 = halves32(a32, b32);
        for (int i = 0; i < 2; i++) h = mix(h, (uint32_t)q32[i]);
        u8x16 a8, b8;
        for (int i = 0; i < 16; i++) a8[i] = (uint8_t)hash(40 * k + i), b8[i] = (uint8_t)hash(40 * k + i + 16);
        u8x16 r8 = shuffles8(a8, b8);
        for (int i = 0; i < 16; i++) h = mix(h, r8[i]);
        f32x4 af = {(float)k, 1.5f * k, -2.25f, 0.125f * k}, bf = {3.0f, -0.5f * k, 7.75f, 1.0f / (k + 1)};
        f32x4 rf = shuffles_float(af, bf);
        for (int i = 0; i < 4; i++) h = mix(h, (uint64_t)(int64_t)(rf[i] * 4096.0f));
    }
    printf("shuffles: %016llx\n", (unsigned long long)h);
    return 0;
}
