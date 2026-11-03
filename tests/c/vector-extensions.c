/* Clang vector extensions over every shape the typed vector lowering handles (ADR-16 Level 1): 128-bit vectors of
 * each lane type, 64-, 32- and 96-bit vectors widened to 128 bits, 256- and 512-bit vectors in fragments, odd lane
 * counts, i1 masks as bool vectors, and the fallback (vectors of __int128, which are scalarized).
 * Output is printed lane by lane; native clang with the same flags is the oracle. Nothing prints an undefined lane,
 * a NaN payload of an arithmetic result or a value LLVM leaves unspecified (the sign of a zero from maxnum). */
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef int8_t i8x16 __attribute__((vector_size(16)));
typedef uint8_t u8x16 __attribute__((vector_size(16)));
typedef int16_t i16x8 __attribute__((vector_size(16)));
typedef uint16_t u16x8 __attribute__((vector_size(16)));
typedef int32_t i32x4 __attribute__((vector_size(16)));
typedef uint32_t u32x4 __attribute__((vector_size(16)));
typedef int64_t i64x2 __attribute__((vector_size(16)));
typedef uint64_t u64x2 __attribute__((vector_size(16)));
typedef float f32x4 __attribute__((vector_size(16)));
typedef double f64x2 __attribute__((vector_size(16)));
typedef int32_t i32x2 __attribute__((vector_size(8)));
typedef float f32x2 __attribute__((vector_size(8)));
typedef int16_t i16x4 __attribute__((vector_size(8)));
typedef uint8_t u8x4 __attribute__((vector_size(4)));
typedef uint8_t u8x8 __attribute__((vector_size(8)));
typedef int32_t i32x8 __attribute__((vector_size(32)));
typedef uint32_t u32x8 __attribute__((vector_size(32)));
typedef int64_t i64x4 __attribute__((vector_size(32)));
typedef float f32x8 __attribute__((vector_size(32)));
typedef double f64x4 __attribute__((vector_size(32)));
typedef uint8_t u8x32 __attribute__((vector_size(32)));
typedef int16_t i16x16 __attribute__((vector_size(32)));
typedef float f32x16 __attribute__((vector_size(64)));
typedef int8_t i8x64 __attribute__((vector_size(64)));
typedef float f32x3 __attribute__((ext_vector_type(3)));
typedef int32_t i32x3 __attribute__((ext_vector_type(3)));
typedef uint8_t u8x3 __attribute__((ext_vector_type(3)));
typedef int32_t i32x6 __attribute__((ext_vector_type(6)));
typedef double f64x3 __attribute__((ext_vector_type(3)));
typedef _Bool b2 __attribute__((ext_vector_type(2)));
typedef _Bool b4 __attribute__((ext_vector_type(4)));
typedef _Bool b8 __attribute__((ext_vector_type(8)));
typedef _Bool b16 __attribute__((ext_vector_type(16)));
typedef _Bool b32 __attribute__((ext_vector_type(32)));
typedef __int128 i128x2 __attribute__((vector_size(32)));

static uint64_t state = 0x9e3779b97f4a7c15ull;
static uint64_t next(void) { state ^= state << 13; state ^= state >> 7; state ^= state << 17; return state; }
static uint64_t digest = 14695981039346656037ull;
__attribute__((noinline)) static void mix(uint64_t value) { digest = (digest ^ value) * 1099511628211ull; }
__attribute__((noinline)) static void show(const char *name, const void *data, size_t size)
{
    const unsigned char *bytes = data;
    printf("%s", name);
    for (size_t index = 0; index < size; index++) printf("%s%02x", index % 4 == 0 ? " " : "", bytes[index]);
    printf("\n");
    for (size_t index = 0; index < size; index++) mix(bytes[index]);
}
#define SHOW(value) show(#value, &(value), sizeof(value))
/* Vectors of 3 and 6 lanes have padding lanes, which are undefined. */
#define SHOWN(value, lanes) show(#value, &(value), (lanes) * sizeof((value)[0]))
/* Lanes of a float vector, with every NaN printed as "nan": only bit operations keep a payload exactly. */
__attribute__((noinline)) static void floats(const char *name, const float *lanes, int count)
{
    printf("%s", name);
    for (int index = 0; index < count; index++)
        if (lanes[index] != lanes[index]) printf(" nan");
        else { uint32_t bits; memcpy(&bits, &lanes[index], 4); printf(" %08x", bits); mix(bits); }
    printf("\n");
}
__attribute__((noinline)) static void doubles(const char *name, const double *lanes, int count)
{
    printf("%s", name);
    for (int index = 0; index < count; index++)
        if (lanes[index] != lanes[index]) printf(" nan");
        else { uint64_t bits; memcpy(&bits, &lanes[index], 8); printf(" %016llx", (unsigned long long)bits); mix(bits); }
    printf("\n");
}
/* A select through a comparison mask, which LLVM forms into a vector select from -O1. */
#define SELECT(mask, a, b, type) ((type)(((mask) & (__typeof__(mask))(a)) | (~(mask) & (__typeof__(mask))(b))))
#define FLOATS(value) floats(#value, (const float *)&(value), sizeof(value) / 4)
#define DOUBLES(value) doubles(#value, (const double *)&(value), sizeof(value) / 8)

static volatile int one = 1;
__attribute__((noinline)) static void fill(void *data, size_t size) { unsigned char *bytes = data; for (size_t index = 0; index < size; index++) bytes[index] = (unsigned char)next(); }
#define RANDOM(type, name) type name; fill(&name, sizeof(name))

/* Function boundaries: vectors and masks by value, in and out. */
__attribute__((noinline)) static i32x8 add8(i32x8 a, i32x8 b) { return a + b * 3; }
__attribute__((noinline)) static f32x2 scale2(f32x2 a, float factor) { return a * factor; }
__attribute__((noinline)) static i32x3 rotate3(i32x3 a) { return a.yzx; }
__attribute__((noinline)) static b8 positive8(i32x8 a) { return __builtin_convertvector(a > 0, b8); }
__attribute__((noinline)) static f32x16 mad16(f32x16 a, f32x16 b, f32x16 c) { return a * b + c; }
__attribute__((noinline)) static i8x64 mix64(i8x64 a, i8x64 b) { return (a ^ b) + (a & b); }
__attribute__((noinline)) static u8x3 add3(u8x3 a, u8x3 b) { return a + b; }
struct carrier { int tag; f32x4 value; i32x2 pair; };
__attribute__((noinline)) static struct carrier carry(struct carrier in) { in.value = in.value * 2.0f; in.pair = in.pair + in.tag; in.tag += 1; return in; }

__attribute__((noinline)) static void arithmetic(void)
{
    RANDOM(i8x16, a8); RANDOM(i8x16, b8v); RANDOM(i16x8, a16); RANDOM(i16x8, b16v); RANDOM(i32x4, a32); RANDOM(i32x4, b32v);
    RANDOM(i64x2, a64); RANDOM(i64x2, b64v);
    i8x16 r8 = a8 + b8v * 3 - (a8 & b8v) ^ (b8v | 5); SHOW(r8);
    i16x8 r16 = a16 * b16v + (a16 >> 3) - (b16v << 2); SHOW(r16);
    u16x8 l16 = (u16x8)a16 >> 5; SHOW(l16);
    i32x4 r32 = a32 * b32v + (a32 >> 7) - (b32v << 9); SHOW(r32);
    u32x4 l32 = (u32x4)a32 >> 31; SHOW(l32);
    i64x2 r64 = a64 * b64v + (a64 >> 40) - (b64v << 1); SHOW(r64);
    u64x2 l64 = (u64x2)a64 >> 63; SHOW(l64);
    i8x16 s8 = a8 >> 7; SHOW(s8);
    u8x16 v8 = (u8x16)a8 << 3; SHOW(v8);
    /* Shifts by a different count in each lane. */
    i32x4 counts = (i32x4){1, 7, 19, 31};
    i32x4 sv = (a32 << counts) ^ (a32 >> counts) ^ (i32x4)((u32x4)a32 >> (u32x4)counts); SHOW(sv);
    i16x8 counts16 = (i16x8){0, 1, 2, 3, 12, 13, 14, 15};
    i16x8 sv16 = (a16 << counts16) + (a16 >> counts16); SHOW(sv16);
    /* Division and remainder lane by lane, with nonzero divisors and no overflow. */
    i32x4 divisor = (b32v & 0xffff) | 1;
    i32x4 q32 = a32 / divisor, m32 = a32 % divisor; SHOW(q32); SHOW(m32);
    u32x4 uq32 = (u32x4)a32 / (u32x4)divisor, um32 = (u32x4)a32 % (u32x4)divisor; SHOW(uq32); SHOW(um32);
    u8x16 d8 = (u8x16)a8 / ((u8x16)b8v | 1); SHOW(d8);
    i64x2 q64 = a64 / ((b64v & 0xffffff) | 1); SHOW(q64);
}

__attribute__((noinline)) static void floating(void)
{
    f32x4 x = {1.5f, -2.25f, 1e30f, -0.0f}, y = {3.0f, 0.5f, 1e-30f, 7.0f};
    f32x4 sum = x + y, product = x * y, quotient = x / y, difference = x - y;
    FLOATS(sum); FLOATS(product); FLOATS(quotient); FLOATS(difference);
    f64x2 p = {1.0 / 3.0, -1e300}, q = {3.0, 1e10};
    f64x2 dp = p * q + p / q; DOUBLES(dp);
    /* fneg flips the sign bit, of zeros and NaNs too. */
    float nan; uint32_t nanbits = 0x7fc12345; memcpy(&nan, &nanbits, 4);
    f32x4 special = {0.0f, -0.0f, nan, -1.0f};
    f32x4 negated = -special; SHOW(negated);
    f64x2 dneg = -(f64x2){0.0, -2.5}; SHOW(dneg);
    f32x4 frem = __builtin_elementwise_fma(x, y, x); FLOATS(frem);
    f32x4 roots = __builtin_elementwise_sqrt((f32x4){4.0f, 2.0f, 1e-40f, 1e38f}); FLOATS(roots);
    f32x4 halves = {0.5f, 1.5f, -2.5f, 3.4999998f};
    f32x4 rounded = __builtin_elementwise_round(halves), even = __builtin_elementwise_roundeven(halves), rint = __builtin_elementwise_rint(halves);
    f32x4 down = __builtin_elementwise_floor(halves), up = __builtin_elementwise_ceil(halves), toward = __builtin_elementwise_trunc(halves);
    FLOATS(rounded); FLOATS(even); FLOATS(rint); FLOATS(down); FLOATS(up); FLOATS(toward);
    f32x4 magnitude = __builtin_elementwise_abs(special); SHOW(magnitude);
    f32x4 signs = __builtin_elementwise_copysign(x, (f32x4){-1.0f, 1.0f, -0.0f, 0.0f}); SHOW(signs);
    f32x4 withnan = {nan, 1.0f, -3.0f, nan}, other = {2.0f, nan, -4.0f, 5.0f};
    f32x4 largest = __builtin_elementwise_max(withnan, other), smallest = __builtin_elementwise_min(withnan, other);
    FLOATS(largest); FLOATS(smallest);
    f32x4 maximum = __builtin_elementwise_maximum(withnan, other), minimum = __builtin_elementwise_minimum((f32x4){-0.0f, 1.0f, 2.0f, 0.0f}, (f32x4){0.0f, 3.0f, nan, -0.0f});
    FLOATS(maximum); SHOW(minimum);
    f64x4 wide = {1.0, -2.0, 3.5, nan}; f64x4 wider = __builtin_elementwise_sqrt(wide * wide); DOUBLES(wider);
}

__attribute__((noinline)) static void comparisons(void)
{
    float nan = __builtin_nanf("");
    f32x4 x = {1.0f, nan, 3.0f, -0.0f}, y = {1.0f, 2.0f, nan, 0.0f};
    i32x4 eq = x == y, ne = x != y, lt = x < y, le = x <= y, gt = x > y, ge = x >= y;
    SHOW(eq); SHOW(ne); SHOW(lt); SHOW(le); SHOW(gt); SHOW(ge);
    i32x4 unordered = ~(x < y) & ~(x >= y); SHOW(unordered);
    RANDOM(i32x4, a); RANDOM(i32x4, b);
    i32x4 sgt = a > b, ugt = (u32x4)a > (u32x4)b, sle = a <= b, ule = (u32x4)a <= (u32x4)b;
    SHOW(sgt); SHOW(ugt); SHOW(sle); SHOW(ule);
    /* Selects through masks: of the compared width, narrower, wider, and of 256 bits. */
    i32x4 picked = SELECT(a > b, a, b, i32x4); SHOW(picked);
    RANDOM(i8x16, c8); RANDOM(i8x16, d8);
    i8x16 max8 = SELECT(c8 > d8, c8, d8, i8x16); SHOW(max8);
    RANDOM(i32x8, e); RANDOM(i32x8, f);
    i32x8 max32 = SELECT(e > f, e, f, i32x8); SHOW(max32);
    RANDOM(i64x4, g); RANDOM(i64x4, h);
    i64x4 min64 = SELECT(g < h, g, h, i64x4); SHOW(min64);
    f64x4 dg = __builtin_convertvector(g >> 20, f64x4), dh = __builtin_convertvector(h >> 20, f64x4);
    f64x4 dmax = SELECT(dg > dh, dg, dh, f64x4); DOUBLES(dmax);
    i16x4 s4a = {1, -2, 3, -4}, s4b = {0, 0, 5, -5};
    i16x4 s4 = SELECT(s4a > s4b, s4a, s4b, i16x4); SHOW(s4);
    f32x2 two = {1.0f, -1.0f}, three = {0.5f, -0.5f};
    f32x2 pick2 = SELECT(two < three, two, three, f32x2); FLOATS(pick2);
}

__attribute__((noinline)) static void conversions(void)
{
    RANDOM(i8x16, bytes); RANDOM(i16x8, shorts); RANDOM(i32x4, ints); RANDOM(i64x4, longs);
    i16x16 sext16 = __builtin_convertvector(bytes, i16x16); SHOW(sext16);
    typedef uint16_t u16x16 __attribute__((vector_size(32)));
    u16x16 zext16 = __builtin_convertvector((u8x16)bytes, u16x16); SHOW(zext16);
    typedef int32_t i32x16 __attribute__((vector_size(64)));
    i32x16 sext32 = __builtin_convertvector(bytes, i32x16); SHOW(sext32);
    i64x2 sext64 = __builtin_convertvector(__builtin_shufflevector(ints, ints, 0, 3), i64x2); SHOW(sext64);
    i64x4 sext4 = __builtin_convertvector(ints, i64x4); SHOW(sext4);
    i8x16 trunc8 = __builtin_convertvector(__builtin_shufflevector(longs, longs, 0, 1, 2, 3, 3, 2, 1, 0, 0, 0, 1, 1, 2, 2, 3, 3), i8x16); SHOW(trunc8);
    i32x4 trunc32 = __builtin_convertvector(longs, i32x4); SHOW(trunc32);
    i8x16 narrow16 = __builtin_convertvector(__builtin_shufflevector(shorts, shorts, 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0), i8x16); SHOW(narrow16);
    f32x4 fi = __builtin_convertvector(ints, f32x4), fu = __builtin_convertvector((u32x4)ints, f32x4); FLOATS(fi); FLOATS(fu);
    f64x4 di = __builtin_convertvector(ints, f64x4), du = __builtin_convertvector((u32x4)ints, f64x4); DOUBLES(di); DOUBLES(du);
    f32x4 fl = __builtin_convertvector(longs, f32x4); FLOATS(fl);
    f32x2 ful = __builtin_convertvector((u64x2)__builtin_shufflevector(longs, longs, 1, 2), f32x2); FLOATS(ful);
    f64x4 dl = __builtin_convertvector(longs, f64x4); DOUBLES(dl);
    typedef uint64_t u64x4 __attribute__((vector_size(32)));
    f64x4 dul = __builtin_convertvector((u64x4)longs, f64x4); DOUBLES(dul);
    f32x8 fs = __builtin_convertvector(shorts, f32x8); FLOATS(fs);
    f32x4 inrange = {-2147483520.0f, 2147483520.0f, -1.75f, 123456.5f};
    i32x4 back = __builtin_convertvector(inrange, i32x4); SHOW(back);
    u32x4 uback = __builtin_convertvector((f32x4){4294967040.0f, 1.5f, 0.0f, 3e9f}, u32x4); SHOW(uback);
    i64x2 lback = __builtin_convertvector((f32x2){-9.2233715e18f, 1e18f}, i64x2); SHOW(lback);
    f64x2 dd = {-2147483648.9, 4294967295.5};
    i32x2 iback = __builtin_convertvector(dd, i32x2); (void)iback;
    typedef uint32_t u32x2 __attribute__((vector_size(8)));
    u32x2 uiback = __builtin_convertvector((f64x2){4294967295.5, 7.25}, u32x2); SHOW(uiback);
    i32x4 cb = __builtin_convertvector((f32x4){-128.5f, 127.0f, 3.9f, -7.9f}, i32x4); SHOW(cb);
    u8x4 tiny = __builtin_convertvector((f32x4){255.0f, 0.0f, 17.5f, 200.9f}, u8x4); SHOW(tiny);
    f32x4 fe = __builtin_convertvector((f64x4){1.0 / 3.0, 1e300, -1e-300, 2.5}, f32x4); FLOATS(fe);
    f64x4 fd = __builtin_convertvector((f32x4){1.0f / 3.0f, 3.4e38f, -1e-40f, 2.5f}, f64x4); DOUBLES(fd);
    /* Bitcasts between lane widths and to scalars. */
    i64x2 asl = (i64x2)ints; SHOW(asl);
    f32x4 asf = (f32x4)ints; uint32_t lane0; memcpy(&lane0, &asf, 4); mix(lane0); printf("asf %08x\n", lane0);
    uint64_t scalar = (uint64_t)(i32x2){7, -9}; printf("scalar %016llx\n", (unsigned long long)scalar); mix(scalar);
    i16x4 fromscalar = (i16x4)0x0123456789abcdefll; SHOW(fromscalar);
    i32x2 pattern = {0, 0x40090000}; double reinterpret; memcpy(&reinterpret, &pattern, 8); printf("reinterpret %.3f\n", reinterpret);
    i32x8 wide = (i32x8)(i64x4){1, -2, 3, -4}; SHOW(wide);
}

__attribute__((noinline)) static void shuffles(void)
{
    RANDOM(i32x4, a); RANDOM(i32x4, b); RANDOM(i8x16, c); RANDOM(i8x16, d); RANDOM(f32x4, f); RANDOM(f64x2, g); RANDOM(i16x8, h);
    i32x4 reverse = __builtin_shufflevector(a, a, 3, 2, 1, 0); SHOW(reverse);
    i32x4 splat = __builtin_shufflevector(a, a, 2, 2, 2, 2); SHOW(splat);
    i32x4 low = __builtin_shufflevector(a, b, 0, 4, 1, 5), high = __builtin_shufflevector(a, b, 2, 6, 3, 7); SHOW(low); SHOW(high);
    i32x4 halves = __builtin_shufflevector(a, b, 0, 1, 4, 5), uppers = __builtin_shufflevector(a, b, 2, 3, 6, 7), mixed = __builtin_shufflevector(a, b, 6, 7, 0, 1);
    SHOW(halves); SHOW(uppers); SHOW(mixed);
    i32x4 blend = __builtin_shufflevector(a, b, 0, 5, 2, 7), any = __builtin_shufflevector(a, b, 7, 0, 5, 2); SHOW(blend); SHOW(any);
    i8x16 bytes = __builtin_shufflevector(c, d, 15, 0, 17, 3, 31, 5, 6, 22, 8, 9, 10, 27, 12, 13, 14, 1); SHOW(bytes);
    i8x16 perm = __builtin_shufflevector(c, c, 1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14); SHOW(perm);
    f32x4 fl = __builtin_shufflevector(f, f, 1, 1, 3, 3); SHOW(fl);
    f64x2 swap = __builtin_shufflevector(g, g, 1, 0); SHOW(swap);
    i16x8 words = __builtin_shufflevector(h, h, 7, 6, 5, 4, 0, 1, 2, 3); SHOW(words);
    i32x8 concat = __builtin_shufflevector(a, b, 0, 1, 2, 3, 4, 5, 6, 7); SHOW(concat);
    i32x8 cross = __builtin_shufflevector(a, b, 7, 0, 6, 1, 5, 2, 4, 3); SHOW(cross);
    i32x4 part = __builtin_shufflevector(cross, cross, 5, 6, 7, 0); SHOW(part);
    i32x2 pair = __builtin_shufflevector(a, a, 3, 1); SHOW(pair);
    i32x4 grow = __builtin_shufflevector(pair, pair, 0, 1, 3, 2); SHOW(grow);
    i32x3 three = __builtin_shufflevector(a, a, 2, 0, 1); SHOWN(three, 3);
    RANDOM(f32x8, e8); RANDOM(f32x8, f8);
    f32x8 deal = __builtin_shufflevector(e8, f8, 0, 8, 2, 10, 4, 12, 6, 14); SHOW(deal);
    f32x4 gather = __builtin_shufflevector(e8, f8, 15, 1, 9, 6); SHOW(gather);
    /* Element access with constant and variable indices. */
    int index = one + 1;
    i32x4 changed = a; changed[index] = 42; changed[0] = changed[index + 1]; SHOW(changed);
    int picked = a[index] + b[3 - index] + cross[index + 4]; printf("picked %d\n", picked); mix((uint32_t)picked);
    i8x16 bytes2 = c; bytes2[index * 5] = 99; printf("lane %d %d\n", bytes2[index * 5], (int)(unsigned char)c[index * 7]); mix((uint32_t)bytes2[10]);
}

__attribute__((noinline)) static void reductions(void)
{
    RANDOM(i32x4, a); RANDOM(u8x16, b); RANDOM(i32x8, c); RANDOM(i16x8, d);
    printf("add %d %u %d %d\n", __builtin_reduce_add(a), (unsigned)__builtin_reduce_add(b), __builtin_reduce_add(c), __builtin_reduce_add(d));
    printf("max %d %u %d %d\n", __builtin_reduce_max(a), (unsigned)__builtin_reduce_max(b), __builtin_reduce_max(c), __builtin_reduce_max(d));
    printf("min %d %u %d %d\n", __builtin_reduce_min(a), (unsigned)__builtin_reduce_min(b), __builtin_reduce_min(c), __builtin_reduce_min(d));
    printf("bits %d %d %d %d\n", __builtin_reduce_and(a), __builtin_reduce_or(a), __builtin_reduce_xor(c), __builtin_reduce_mul(a));
    f32x4 f = {1.0f, -7.5f, 3.25f, 2.0f};
    printf("fmax %.3f fmin %.3f\n", __builtin_reduce_max(f), __builtin_reduce_min(f));
    f32x8 g = {1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.5f};
    f32x8 acc = g; for (int index = 0; index < 5 + one; index++) acc = acc * 0.5f + g; FLOATS(acc);
    i32x4 total = {0}; for (int index = 0; index < 100 + one; index++) total += (i32x4){index, -index, index * index, 1}; SHOW(total);
}

__attribute__((noinline)) static void elementwise(void)
{
    RANDOM(i8x16, a); RANDOM(i8x16, b); RANDOM(i16x8, c); RANDOM(i16x8, d); RANDOM(i32x4, e); RANDOM(i32x4, f); RANDOM(i64x2, g);
    i8x16 absolute = __builtin_elementwise_abs(a); SHOW(absolute);
    i32x4 abs32 = __builtin_elementwise_abs(e); SHOW(abs32);
    i64x2 abs64 = __builtin_elementwise_abs(g); SHOW(abs64);
    i8x16 smax = __builtin_elementwise_max(a, b), smin = __builtin_elementwise_min(a, b); SHOW(smax); SHOW(smin);
    u8x16 umax = __builtin_elementwise_max((u8x16)a, (u8x16)b), umin = __builtin_elementwise_min((u8x16)a, (u8x16)b); SHOW(umax); SHOW(umin);
    u32x4 umax32 = __builtin_elementwise_max((u32x4)e, (u32x4)f); SHOW(umax32);
    i64x2 smax64 = __builtin_elementwise_max(g, (i64x2){0, -1}); SHOW(smax64);
    i8x16 sadd = __builtin_elementwise_add_sat(a, b), ssub = __builtin_elementwise_sub_sat(a, b); SHOW(sadd); SHOW(ssub);
    u8x16 uadd = __builtin_elementwise_add_sat((u8x16)a, (u8x16)b), usub = __builtin_elementwise_sub_sat((u8x16)a, (u8x16)b); SHOW(uadd); SHOW(usub);
    i16x8 sadd16 = __builtin_elementwise_add_sat(c, d); SHOW(sadd16);
    u16x8 usub16 = __builtin_elementwise_sub_sat((u16x8)c, (u16x8)d); SHOW(usub16);
    i32x4 sadd32 = __builtin_elementwise_add_sat(e, f); SHOW(sadd32);
    u32x4 usub32 = __builtin_elementwise_sub_sat((u32x4)e, (u32x4)f); SHOW(usub32);
    /* Bit counts and reversals of every lane width; a zero lane takes the second operand of clzg and ctzg. */
    u8x16 pop8 = __builtin_elementwise_popcount((u8x16)a), rev8 = __builtin_elementwise_bitreverse((u8x16)a); SHOW(pop8); SHOW(rev8);
    u16x8 zeros16 = (u16x8)c & (u16x8){0xffff, 0, 0xff00, 0x00ff, 0xffff, 0x8000, 1, 0};
    u16x8 clz16 = __builtin_elementwise_clzg(zeros16, ((u16x8){0} + 16)), ctz16 = __builtin_elementwise_ctzg(zeros16, ((u16x8){0} + 16)); SHOW(clz16); SHOW(ctz16);
    u32x4 pop32 = __builtin_elementwise_popcount((u32x4)e), clz32 = __builtin_elementwise_clzg((u32x4)e | 1), ctz32 = __builtin_elementwise_ctzg((u32x4)e | 0x80000000u);
    SHOW(pop32); SHOW(clz32); SHOW(ctz32);
    typedef uint64_t u64x2e __attribute__((vector_size(16)));
    u64x2e pop64 = __builtin_elementwise_popcount((u64x2e)g), rev64 = __builtin_elementwise_bitreverse((u64x2e)g), clz64 = __builtin_elementwise_clzg((u64x2e)g >> 7, ((u64x2e){0} + 64));
    SHOW(pop64); SHOW(rev64); SHOW(clz64);
    /* Funnel shifts by one count for all lanes (rotations included) and by a count per lane. */
    u32x4 rotl = __builtin_elementwise_fshl((u32x4)e, (u32x4)e, ((u32x4){0} + 7)), funl = __builtin_elementwise_fshl((u32x4)e, (u32x4)f, ((u32x4){0} + 32));
    u32x4 funr = __builtin_elementwise_fshr((u32x4)e, (u32x4)f, (u32x4){0, 1, 31, 45}); SHOW(rotl); SHOW(funl); SHOW(funr);
    u16x8 funr16 = __builtin_elementwise_fshr((u16x8)c, (u16x8)d, ((u16x8){0} + 3)); SHOW(funr16);
    u64x2e funl64 = __builtin_elementwise_fshl((u64x2e)g, (u64x2e)g * 3, ((u64x2e){0} + 13)); SHOW(funl64);
}

/* No CIL vector has __int128 lanes: the function is scalarized by LLVM, as all vector code was before. */
__attribute__((noinline)) static void fallback(const i32x8 *x)
{
    i128x2 wide = {(__int128)(*x)[0] << 70, (__int128)(*x)[1] * 12345};
    wide = wide * 3 + (wide >> 5) - (wide << 9);
    unsigned long long halves[4]; memcpy(halves, &wide, 32); printf("i128 %llx %llx %llx %llx\n", halves[0], halves[1], halves[2], halves[3]); mix(halves[1]);
}

__attribute__((noinline)) static void masks(void)
{
    RANDOM(i32x8, a); RANDOM(i32x8, b); RANDOM(i8x16, c); RANDOM(i64x4, d);
    b8 m8 = __builtin_convertvector(a > b, b8);
    unsigned char packed; memcpy(&packed, &m8, 1); printf("mask8 %02x\n", packed); mix(packed);
    b16 m16 = __builtin_convertvector(c < 0, b16);
    uint16_t packed16; memcpy(&packed16, &m16, 2); printf("mask16 %04x\n", packed16); mix(packed16);
    b4 m4 = __builtin_convertvector(d > 0, b4);
    unsigned char packed4; memcpy(&packed4, &m4, 1); printf("mask4 %x\n", packed4 & 15); mix(packed4 & 15);
    b2 m2 = __builtin_convertvector((f32x2){1.0f, -1.0f} > 0.0f, b2);
    unsigned char packed2; memcpy(&packed2, &m2, 1); printf("mask2 %x\n", packed2 & 3);
    b8 stored[3]; stored[0] = m8; stored[1] = ~m8; stored[2] = m8 & positive8(b);
    unsigned char bytes[3]; memcpy(bytes, stored, 3); printf("stored %02x %02x %02x\n", bytes[0], bytes[1], bytes[2]); mix(bytes[2]);
    b8 loaded = stored[(one + 1) % 3];
    i32x8 selected = __builtin_convertvector(loaded, i32x8); SHOW(selected);
    int lanes = 0; for (int index = 0; index < 8; index++) lanes += loaded[index] ? index : 0; printf("lanes %d\n", lanes);
    b8 fromint; unsigned char pattern = 0xa5; memcpy(&fromint, &pattern, 1);
    i16x8 expanded = __builtin_convertvector(fromint, i16x8); SHOW(expanded);
    b32 m32 = __builtin_convertvector(__builtin_shufflevector(c, c, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0) > 3, b32);
    uint32_t packed32; memcpy(&packed32, &m32, 4); printf("mask32 %08x\n", packed32); mix(packed32);
    printf("any %d all %d\n", __builtin_reduce_or(m8), __builtin_reduce_and(m8 | ~m8));
}

__attribute__((noinline)) static void memory(void)
{
    /* Partial vectors touch exactly their bytes; the canary after each must survive. */
    struct { u8x3 three; unsigned char canary; } small = {{1, 2, 3}, 0x5a};
    small.three = add3(small.three, (u8x3){10, 20, 250}); printf("u8x3 %d %d %d canary %02x\n", small.three.x, small.three.y, small.three.z, small.canary);
    struct { int32_t lanes[3]; int32_t canary; } packed3 = {{7, 8, 9}, 0x5a5a};
    i32x3 v3; memcpy(&v3, packed3.lanes, 12); v3 = rotate3(v3) * 2; memcpy(packed3.lanes, &v3, 12);
    printf("i32x3 %d %d %d canary %x\n", packed3.lanes[0], packed3.lanes[1], packed3.lanes[2], packed3.canary);
    f32x3 vf = {1.0f, 2.0f, 3.0f}; f32x3 *heap = (f32x3 *)__builtin_alloca(sizeof(f32x3)); *heap = vf * vf; floats("heap", (const float *)heap, 3);
    f64x3 d3 = {1.5, 2.5, 3.5}; d3 = d3 + d3.zyx; doubles("d3", (const double *)&d3, 3);
    i32x6 six = {1, 2, 3, 4, 5, 6}; six = six * six - (i32x6){6, 5, 4, 3, 2, 1}; SHOWN(six, 6);
    struct __attribute__((packed)) { char c; i32x4 v; f64x2 w; } unaligned = {'x', {1, -2, 3, -4}, {0.5, -0.25}};
    unaligned.v = unaligned.v * unaligned.v + 1; unaligned.w = unaligned.w * 4.0;
    i32x4 uv = unaligned.v; f64x2 uw = unaligned.w; SHOW(uv); DOUBLES(uw);
    volatile i32x4 shared = {1, 2, 3, 4}; i32x4 copy = shared; shared = copy + copy; copy = shared; SHOW(copy);
    i32x2 pairs[4] = {{1, 2}, {3, 4}, {5, 6}, {7, 8}};
    for (int index = 0; index < 4; index++) pairs[index] = pairs[index] * (index + one) + pairs[(index + 1) & 3];
    SHOW(pairs);
    u8x8 eight; memcpy(&eight, "abcdefgh", 8); eight = eight + 1; printf("u8x8 %.8s\n", (char *)&eight);
    struct carrier carried = carry((struct carrier){5, {1.0f, 2.0f, 3.0f, 4.0f}, {10, 20}});
    printf("carry %d", carried.tag); FLOATS(carried.value); SHOW(carried.pair);
}

__attribute__((noinline)) static void boundaries(void)
{
    RANDOM(i32x8, a); RANDOM(i32x8, b); RANDOM(i8x64, c); RANDOM(i8x64, d);
    i32x8 sum = add8(a, b); SHOW(sum);
    f32x2 scaled = scale2((f32x2){1.5f, -2.0f}, 3.0f); FLOATS(scaled);
    f32x16 x, y, z; for (int index = 0; index < 16; index++) { x[index] = index * 0.5f; y[index] = 16 - index; z[index] = -index; }
    f32x16 fused = mad16(x, y, z); FLOATS(fused);
    i8x64 mixed = mix64(c, d); SHOW(mixed);
}

int main(void)
{
    arithmetic();
    floating();
    comparisons();
    conversions();
    shuffles();
    reductions();
    elementwise();
    masks();
    memory();
    boundaries();
    RANDOM(i32x8, x); fallback(&x);
    printf("digest %016llx\n", (unsigned long long)digest);
    return 0;
}
