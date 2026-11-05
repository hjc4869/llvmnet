/* Widening reductions (SV-21): sums of absolute differences of bytes and sums of products of 16-bit values in the
 * shapes the loop and SLP vectorizers give them, which llvmnet runs as psadbw and pmaddwd, and look-alikes it must
 * leave alone (products of unsigned 16-bit values, accumulators whose lanes the program reads). Prints one line per
 * function and length; tests/vector-reductions.sh compares it with native clang's output. */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define NOINLINE __attribute__((noinline))
#define N 1031

static uint8_t ua[N + 64], ub[N + 64];
static int8_t sa[N], sb[N];
static int16_t ha[N], hb[N];
static uint16_t wa[N];

static uint32_t hash(uint32_t i)
{
    uint32_t h = i * 2654435761u + 0x9e3779b9u;
    h ^= h >> 15;
    h *= 0x85ebca6bu;
    h ^= h >> 13;
    return h;
}

NOINLINE static uint32_t sad_u32(const uint8_t *a, const uint8_t *b, int n)
{
    uint32_t s = 0;
    for (int i = 0; i < n; i++) s += (uint32_t)abs(a[i] - b[i]);
    return s;
}

NOINLINE static uint64_t sad_u64(const uint8_t *a, const uint8_t *b, int n)
{
    uint64_t s = 0;
    for (int i = 0; i < n; i++) s += (uint64_t)abs(a[i] - b[i]);
    return s;
}

NOINLINE static uint16_t sad_u16(const uint8_t *a, const uint8_t *b, int n)
{
    uint16_t s = 0;
    for (int i = 0; i < n; i++) s = (uint16_t)(s + abs(a[i] - b[i]));
    return s;
}

NOINLINE static int sad_select(const uint8_t *a, const uint8_t *b, int n)
{
    int s = 0;
    for (int i = 0; i < n; i++) {
        int d = a[i] - b[i];
        s += d < 0 ? -d : d;
    }
    return s;
}

/* ffmpeg's pix_abs16_c: sixteen terms per row, which the SLP vectorizer reduces as one vector. */
NOINLINE static int pix_abs16(const uint8_t *a, const uint8_t *b, int stride, int h)
{
    int s = 0;
    for (int y = 0; y < h; y++) {
        s += abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2]) + abs(a[3] - b[3]);
        s += abs(a[4] - b[4]) + abs(a[5] - b[5]) + abs(a[6] - b[6]) + abs(a[7] - b[7]);
        s += abs(a[8] - b[8]) + abs(a[9] - b[9]) + abs(a[10] - b[10]) + abs(a[11] - b[11]);
        s += abs(a[12] - b[12]) + abs(a[13] - b[13]) + abs(a[14] - b[14]) + abs(a[15] - b[15]);
        a += stride;
        b += stride;
    }
    return s;
}

NOINLINE static int pix_abs8(const uint8_t *a, const uint8_t *b, int stride, int h)
{
    int s = 0;
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < 8; x++) s += abs(a[x] - b[x]);
        a += stride;
        b += stride;
    }
    return s;
}

NOINLINE static uint32_t dot_i16(const int16_t *a, const int16_t *b, int n)
{
    uint32_t s = 0;
    for (int i = 0; i < n; i++) s += (uint32_t)(a[i] * b[i]);
    return s;
}

NOINLINE static uint32_t dot_u8_i16(const uint8_t *a, const int16_t *b, int n)
{
    uint32_t s = 0;
    for (int i = 0; i < n; i++) s += (uint32_t)(a[i] * b[i]);
    return s;
}

NOINLINE static int32_t dot_i8(const int8_t *a, const int8_t *b, int n)
{
    int32_t s = 0;
    for (int i = 0; i < n; i++) s += a[i] * b[i];
    return s;
}

NOINLINE static uint32_t dot_const(const int16_t *a, int n)
{
    uint32_t s = 0;
    for (int i = 0; i < n; i++) s += (uint32_t)(a[i] * -1234);
    return s;
}

NOINLINE static uint32_t sum_squares(const int16_t *a, int n)
{
    uint32_t s = 0;
    for (int i = 0; i < n; i++) s += (uint32_t)(a[i] * a[i]);
    return s;
}

/* Not a pmaddwd: an unsigned 16-bit value does not fit a signed 16-bit lane. */
NOINLINE static uint32_t dot_u16(const uint16_t *a, const int16_t *b, int n)
{
    uint32_t s = 0;
    for (int i = 0; i < n; i++) s += (uint32_t)(a[i] * b[i]);
    return s;
}

/* Four taps of a filter, as SLP vectorizes them. */
NOINLINE static uint32_t taps4(const int16_t *x, const int16_t *c)
{
    return (uint32_t)(x[0] * c[0]) + (uint32_t)(x[1] * c[1]) + (uint32_t)(x[2] * c[2]) + (uint32_t)(x[3] * c[3]);
}

NOINLINE static uint32_t taps8(const int16_t *x, const int16_t *c)
{
    uint32_t s = 0;
    for (int i = 0; i < 8; i++) s += (uint32_t)(x[i] * c[i]);
    return s;
}

typedef int32_t i32x4 __attribute__((vector_size(16)));
typedef uint8_t u8x4 __attribute__((vector_size(4)));
typedef int16_t i16x8 __attribute__((vector_size(16)));
typedef int32_t i32x8 __attribute__((vector_size(32)));
typedef uint32_t u32x8 __attribute__((vector_size(32)));

/* Vector extensions: an accumulator only the reduction reads, and the same accumulator whose lanes are also stored,
 * which must keep the literal lanes. */
NOINLINE static int sad_vector(const uint8_t *a, const uint8_t *b, int n, int32_t *lanes)
{
    i32x4 acc = {0, 0, 0, 0};
    for (int i = 0; i + 4 <= n; i += 4) {
        u8x4 x, y;
        memcpy(&x, a + i, 4);
        memcpy(&y, b + i, 4);
        acc += __builtin_elementwise_abs(__builtin_convertvector(x, i32x4) - __builtin_convertvector(y, i32x4));
    }
    if (lanes != NULL) memcpy(lanes, &acc, sizeof acc);
    return __builtin_reduce_add(acc);
}

NOINLINE static uint32_t dot_vector(const int16_t *a, const int16_t *b, int n, uint32_t *lanes)
{
    u32x8 acc = {0};
    for (int i = 0; i + 8 <= n; i += 8) {
        i16x8 x, y;
        memcpy(&x, a + i, 16);
        memcpy(&y, b + i, 16);
        acc += (u32x8)(__builtin_convertvector(x, i32x8) * __builtin_convertvector(y, i32x8));
    }
    if (lanes != NULL) memcpy(lanes, &acc, sizeof acc);
    return __builtin_reduce_add(acc);
}

NOINLINE static int sad_vector_sum(const uint8_t *a, const uint8_t *b, int n)
{
    i32x4 acc = {0, 0, 0, 0};
    for (int i = 0; i + 4 <= n; i += 4) {
        u8x4 x, y;
        memcpy(&x, a + i, 4);
        memcpy(&y, b + i, 4);
        acc += __builtin_elementwise_abs(__builtin_convertvector(x, i32x4) - __builtin_convertvector(y, i32x4));
    }
    return __builtin_reduce_add(acc);
}

NOINLINE static uint32_t dot_vector_sum(const int16_t *a, const int16_t *b, int n)
{
    u32x8 acc = {0};
    for (int i = 0; i + 8 <= n; i += 8) {
        i16x8 x, y;
        memcpy(&x, a + i, 16);
        memcpy(&y, b + i, 16);
        acc += (u32x8)(__builtin_convertvector(x, i32x8) * __builtin_convertvector(y, i32x8));
    }
    return __builtin_reduce_add(acc);
}

int main(void)
{
    for (int i = 0; i < N + 64; i++) {
        uint32_t h = hash((uint32_t)i), g = hash((uint32_t)i + 0x10000u);
        ua[i] = (uint8_t)h;
        ub[i] = (uint8_t)g;
        if (i % 97 == 0) ua[i] = 0, ub[i] = 255;
    }
    for (int i = 0; i < N; i++) {
        uint32_t h = hash((uint32_t)i + 0x20000u), g = hash((uint32_t)i + 0x30000u);
        sa[i] = (int8_t)h;
        sb[i] = (int8_t)g;
        ha[i] = (int16_t)h;
        hb[i] = (int16_t)g;
        wa[i] = (uint16_t)(h >> 16);
        /* Runs of the extremes, where pmaddwd's pair of -32768 * -32768 wraps as the 32-bit sum does. */
        if (i % 64 < 8) ha[i] = hb[i] = -32768;
        if (i % 64 == 9) ha[i] = 32767, hb[i] = -32768;
        if (i % 53 == 0) wa[i] = 65535;
    }
    static const int lengths[] = {0, 1, 3, 4, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 100, 255, 256, 1000, N};
    for (size_t k = 0; k < sizeof lengths / sizeof lengths[0]; k++) {
        int n = lengths[k];
        printf("n=%d sad_u32=%u sad_u64=%llu sad_u16=%u sad_select=%d\n", n, sad_u32(ua, ub, n), (unsigned long long)sad_u64(ua + 1, ub, n),
               sad_u16(ua, ub + 3, n), sad_select(ua + 5, ub + 2, n));
        printf("n=%d dot_i16=%u dot_u8_i16=%u dot_i8=%d dot_const=%u sum_squares=%u dot_u16=%u\n", n, dot_i16(ha, hb, n), dot_u8_i16(ua, hb, n),
               dot_i8(sa, sb, n), dot_const(ha, n), sum_squares(ha, n), dot_u16(wa, hb, n));
        printf("n=%d sad_vector=%d dot_vector=%u\n", n, sad_vector_sum(ua, ub, n), dot_vector_sum(ha, hb, n));
    }
    for (int h = 1; h <= 16; h *= 2)
        printf("h=%d pix_abs16=%d pix_abs8=%d\n", h, pix_abs16(ua, ub, 61, h), pix_abs8(ua + 7, ub + 1, 37, h));
    for (int i = 0; i + 8 <= N; i += 129) printf("i=%d taps4=%u taps8=%u\n", i, taps4(ha + i, hb), taps8(ha + i, hb + 8));
    int32_t lanes[4];
    int total = sad_vector(ua, ub, 1000, lanes);
    printf("sad lanes %d %d %d %d total %d\n", lanes[0], lanes[1], lanes[2], lanes[3], total);
    uint32_t products[8];
    uint32_t sum = dot_vector(ha, hb, 1000, products);
    printf("dot lanes %u %u %u %u %u %u %u %u total %u\n", products[0], products[1], products[2], products[3], products[4], products[5], products[6],
           products[7], sum);
    return 0;
}
