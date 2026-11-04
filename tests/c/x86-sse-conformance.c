/* Conformance test of the SSE to SSE4.2 target intrinsics (llvm.x86.*, ADR-16 Level 2). Each intrinsic is declared under
   its LLVM name with an asm label, so a call reaches the backend as the intrinsic itself, natively as its instruction and
   under llvmnet as its runtime helper. Each runs on random and special inputs (NaNs with payloads, signaling NaNs,
   denormals, infinities, the integer bounds and the conversion and rounding boundaries) and, for an immediate operand, at
   every value of it. One line per intrinsic: its name, a hash of every result bit, and a hash with NaN results replaced
   by one canonical NaN, which the runs on hosts whose default NaN differs from x86's compare instead (the argument
   "portable" prints only that one). rcpps and rsqrtps, whose bits differ between processors, print the number of results
   outside the instructions' documented error. */
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef float v4f __attribute__((vector_size(16)));
typedef double v2d __attribute__((vector_size(16)));
typedef long long v2q __attribute__((vector_size(16)));
typedef int v4d __attribute__((vector_size(16)));
typedef short v8w __attribute__((vector_size(16)));
typedef char v16b __attribute__((vector_size(16)));

#define ROUNDS 3000

static uint64_t state = 0x9e3779b97f4a7c15ull;
static uint64_t next(void)
{
    state ^= state >> 12;
    state ^= state << 25;
    state ^= state >> 27;
    return state * 0x2545f4914f6cdd1dull;
}

static float float_bits(uint32_t bits) { float value; memcpy(&value, &bits, 4); return value; }
static double double_bits(uint64_t bits) { double value; memcpy(&value, &bits, 8); return value; }

static const uint32_t special_floats[] = {
    0x00000000, 0x80000000, 0x7f800000, 0xff800000, 0x7fc00000, 0xffc00000, 0x7fc12345, 0xffd54321, 0x7fa00001, 0xff800001,
    0x00000001, 0x807fffff, 0x00800000, 0x80800000, 0x7f7fffff, 0xff7fffff, 0x3f000000, 0xbf000000, 0x3fc00000, 0xbfc00000,
    0x40200000, 0xc0200000, 0x3f800000, 0xbf800000, 0x4f000000, 0xcf000000, 0x4effffff, 0xcf000001, 0x5f000000, 0xdf000000,
    0x5effffff, 0xdf000001, 0x7e800000, 0x7e7ffd00, 0x7e800300, 0xfe800000, 0x4b000000, 0x4b7fffff, 0x3effffff, 0x3f7fffff,
};
static const uint64_t special_doubles[] = {
    0x0000000000000000, 0x8000000000000000, 0x7ff0000000000000, 0xfff0000000000000, 0x7ff8000000000000, 0xfff8000000000000,
    0x7ff8123456789abc, 0xfffc000000000001, 0x7ff4000000000001, 0xfff0000000000001, 0x0000000000000001, 0x800fffffffffffff,
    0x0010000000000000, 0x7fefffffffffffff, 0xffefffffffffffff, 0x3fe0000000000000, 0xbfe0000000000000, 0x3ff8000000000000,
    0x4004000000000000, 0xc004000000000000, 0x41dfffffffc00000, 0x41dfffffffe00000, 0x41e0000000000000, 0xc1e0000000000000,
    0xc1e0000000100000, 0xc1e0000000200000, 0x43e0000000000000, 0xc3e0000000000000, 0x43dfffffffffffff, 0x4330000000000000,
    0x3fdfffffffffffff, 0x3fefffffffffffff,
};

/* A float: any bit pattern, a special value, a multiple of a small power of two (halves for rounding), or a large value. */
static float random_float(void)
{
    uint64_t choice = next();
    switch (choice % 8) {
    case 0: case 1: return float_bits((uint32_t)(choice >> 32));
    case 2: case 3: return float_bits(special_floats[(choice >> 8) % (sizeof special_floats / sizeof *special_floats)]);
    case 7: return (float)(int64_t)((choice >> 8) % 17179869184ull) - 8589934592.0f;
    default: return (float)((int64_t)((choice >> 8) % 2001) - 1000) / (float)(1u << ((choice >> 20) % 8));
    }
}

static double random_double(void)
{
    uint64_t choice = next();
    switch (choice % 8) {
    case 0: case 1: return double_bits(next());
    case 2: case 3: return double_bits(special_doubles[(choice >> 8) % (sizeof special_doubles / sizeof *special_doubles)]);
    case 7: return (double)(int64_t)next() / (double)(1ull << ((choice >> 8) % 32));
    default: return (double)((int64_t)((choice >> 8) % 2001) - 1000) / (double)(1u << ((choice >> 20) % 8));
    }
}

static v4f random_v4f(void) { v4f value = {random_float(), random_float(), random_float(), random_float()}; return value; }
static v2d random_v2d(void) { v2d value = {random_double(), random_double()}; return value; }

/* Integer lanes: random bits, or the bounds and small values the saturating and multiplying operations care about. */
static v16b random_bytes(void)
{
    static const unsigned char special[] = {0, 1, 0x7f, 0x80, 0x81, 0xfe, 0xff, 0x40};
    unsigned char bytes[16];
    for (int lane = 0; lane < 16; lane++) {
        uint64_t choice = next();
        bytes[lane] = choice % 4 == 0 ? special[(choice >> 8) % 8] : (unsigned char)(choice >> 32);
    }
    v16b value;
    memcpy(&value, bytes, 16);
    return value;
}

static v8w random_words(void)
{
    static const unsigned short special[] = {0, 1, 0x7fff, 0x8000, 0x8001, 0xffff, 0x00ff, 0xff00, 0x0080, 0xff80, 0x4000, 0xc000};
    unsigned short words[8];
    for (int lane = 0; lane < 8; lane++) {
        uint64_t choice = next();
        words[lane] = choice % 4 == 0 ? special[(choice >> 8) % 12] : (unsigned short)(choice >> 32);
    }
    v8w value;
    memcpy(&value, words, 16);
    return value;
}

static v4d random_dwords(void)
{
    static const unsigned special[] = {0, 1, 0x7fffffff, 0x80000000, 0x80000001, 0xffffffff, 0x0000ffff, 0xffff0000, 0x00008000, 0xffff7fff};
    unsigned dwords[4];
    for (int lane = 0; lane < 4; lane++) {
        uint64_t choice = next();
        dwords[lane] = choice % 4 == 0 ? special[(choice >> 8) % 10] : (unsigned)(choice >> 32);
    }
    v4d value;
    memcpy(&value, dwords, 16);
    return value;
}

static v2q random_quads(void) { v4d value = random_dwords(); v2q result; memcpy(&result, &value, 16); return result; }

/* A shift count vector: mostly 0 to 70 in the low 64 bits, sometimes far beyond, with random upper bits the shift ignores. */
static v2q random_count(void)
{
    uint64_t choice = next();
    v2q value = {(long long)(choice % 4 == 0 ? next() >> (choice >> 8) % 64 : (choice >> 8) % 71), (long long)next()};
    return value;
}

static int random_scalar_count(void)
{
    uint64_t choice = next();
    return choice % 4 == 0 ? (int)(uint32_t)next() : (int)((choice >> 8) % 71);
}

/* A string operand: a small alphabet with zero elements, so that the comparisons match, end early and meet the bounds. */
static v16b random_string(int words)
{
    static const unsigned char alphabet[] = {'a', 'b', 'c', 'a', 0x80, 0xff, 0x7f, 'z'};
    unsigned char bytes[16];
    uint64_t choice = next();
    for (int lane = 0; lane < 16; lane++) {
        uint64_t element = next();
        if (choice % 4 == 0) bytes[lane] = (unsigned char)element;
        else bytes[lane] = element % 10 == 0 ? 0 : alphabet[(element >> 8) % 8];
        if (words && lane % 2 == 1 && choice % 4 != 0) bytes[lane] = element % 3 == 0 ? bytes[lane - 1] : 0;
    }
    v16b value;
    memcpy(&value, bytes, 16);
    return value;
}

static int random_length(void)
{
    static const int special[] = {-2147483647 - 1, 2147483647, -17, 17, -9, 9, 16, -16, 8, -8, 0};
    uint64_t choice = next();
    return choice % 4 == 0 ? special[(choice >> 8) % 11] : (int)((choice >> 8) % 41) - 20;
}

static uint64_t exact, portable;
static int portable_only;
static void mix(uint64_t *hash, const void *data, size_t size)
{
    const unsigned char *bytes = data;
    for (size_t index = 0; index < size; index++) {
        *hash ^= bytes[index];
        *hash *= 0x100000001b3ull;
    }
}

static void begin(void) { exact = portable = 0xcbf29ce484222325ull; }
static void end(const char *name)
{
    if (portable_only) printf("%s %016llx\n", name, (unsigned long long)portable);
    else printf("%s %016llx %016llx\n", name, (unsigned long long)exact, (unsigned long long)portable);
}
static void hash_bits(const void *data, size_t size) { mix(&exact, data, size); mix(&portable, data, size); }
static void hash_int(long long value) { hash_bits(&value, 8); }

static void hash_v4f(v4f value)
{
    mix(&exact, &value, 16);
    for (int lane = 0; lane < 4; lane++) {
        float element = value[lane];
        if (element != element) element = float_bits(0x7fc00000);
        mix(&portable, &element, 4);
    }
}

static void hash_v2d(v2d value)
{
    mix(&exact, &value, 16);
    for (int lane = 0; lane < 2; lane++) {
        double element = value[lane];
        if (element != element) element = double_bits(0x7ff8000000000000);
        mix(&portable, &element, 8);
    }
}

#define I(name) __asm__("llvm.x86." name)
v4f rcp_ps(v4f) I("sse.rcp.ps");
v4f rcp_ss(v4f) I("sse.rcp.ss");
v4f rsqrt_ps(v4f) I("sse.rsqrt.ps");
v4f rsqrt_ss(v4f) I("sse.rsqrt.ss");
v4f min_ps(v4f, v4f) I("sse.min.ps");
v4f min_ss(v4f, v4f) I("sse.min.ss");
v4f max_ps(v4f, v4f) I("sse.max.ps");
v4f max_ss(v4f, v4f) I("sse.max.ss");
v4f cmp_ps(v4f, v4f, char) I("sse.cmp.ps");
v4f cmp_ss(v4f, v4f, char) I("sse.cmp.ss");
int comieq_ss(v4f, v4f) I("sse.comieq.ss");
int comilt_ss(v4f, v4f) I("sse.comilt.ss");
int comile_ss(v4f, v4f) I("sse.comile.ss");
int comigt_ss(v4f, v4f) I("sse.comigt.ss");
int comige_ss(v4f, v4f) I("sse.comige.ss");
int comineq_ss(v4f, v4f) I("sse.comineq.ss");
int ucomieq_ss(v4f, v4f) I("sse.ucomieq.ss");
int ucomilt_ss(v4f, v4f) I("sse.ucomilt.ss");
int ucomile_ss(v4f, v4f) I("sse.ucomile.ss");
int ucomigt_ss(v4f, v4f) I("sse.ucomigt.ss");
int ucomige_ss(v4f, v4f) I("sse.ucomige.ss");
int ucomineq_ss(v4f, v4f) I("sse.ucomineq.ss");
int cvtss2si(v4f) I("sse.cvtss2si");
long long cvtss2si64(v4f) I("sse.cvtss2si64");
int cvttss2si(v4f) I("sse.cvttss2si");
long long cvttss2si64(v4f) I("sse.cvttss2si64");
void stmxcsr(void *) I("sse.stmxcsr");
int movmsk_ps(v4f) I("sse.movmsk.ps");
v2d min_pd(v2d, v2d) I("sse2.min.pd");
v2d min_sd(v2d, v2d) I("sse2.min.sd");
v2d max_pd(v2d, v2d) I("sse2.max.pd");
v2d max_sd(v2d, v2d) I("sse2.max.sd");
v2d cmp_pd(v2d, v2d, char) I("sse2.cmp.pd");
v2d cmp_sd(v2d, v2d, char) I("sse2.cmp.sd");
int comieq_sd(v2d, v2d) I("sse2.comieq.sd");
int comilt_sd(v2d, v2d) I("sse2.comilt.sd");
int comile_sd(v2d, v2d) I("sse2.comile.sd");
int comigt_sd(v2d, v2d) I("sse2.comigt.sd");
int comige_sd(v2d, v2d) I("sse2.comige.sd");
int comineq_sd(v2d, v2d) I("sse2.comineq.sd");
int ucomieq_sd(v2d, v2d) I("sse2.ucomieq.sd");
int ucomilt_sd(v2d, v2d) I("sse2.ucomilt.sd");
int ucomile_sd(v2d, v2d) I("sse2.ucomile.sd");
int ucomigt_sd(v2d, v2d) I("sse2.ucomigt.sd");
int ucomige_sd(v2d, v2d) I("sse2.ucomige.sd");
int ucomineq_sd(v2d, v2d) I("sse2.ucomineq.sd");
v8w pmulhu_w(v8w, v8w) I("sse2.pmulhu.w");
v8w pmulh_w(v8w, v8w) I("sse2.pmulh.w");
v4d pmadd_wd(v8w, v8w) I("sse2.pmadd.wd");
v16b pavg_b(v16b, v16b) I("sse2.pavg.b");
v8w pavg_w(v8w, v8w) I("sse2.pavg.w");
v2q psad_bw(v16b, v16b) I("sse2.psad.bw");
v8w psll_w(v8w, v8w) I("sse2.psll.w");
v4d psll_d(v4d, v4d) I("sse2.psll.d");
v2q psll_q(v2q, v2q) I("sse2.psll.q");
v8w psrl_w(v8w, v8w) I("sse2.psrl.w");
v4d psrl_d(v4d, v4d) I("sse2.psrl.d");
v2q psrl_q(v2q, v2q) I("sse2.psrl.q");
v8w psra_w(v8w, v8w) I("sse2.psra.w");
v4d psra_d(v4d, v4d) I("sse2.psra.d");
v8w pslli_w(v8w, int) I("sse2.pslli.w");
v4d pslli_d(v4d, int) I("sse2.pslli.d");
v2q pslli_q(v2q, int) I("sse2.pslli.q");
v8w psrli_w(v8w, int) I("sse2.psrli.w");
v4d psrli_d(v4d, int) I("sse2.psrli.d");
v2q psrli_q(v2q, int) I("sse2.psrli.q");
v8w psrai_w(v8w, int) I("sse2.psrai.w");
v4d psrai_d(v4d, int) I("sse2.psrai.d");
v4d cvtpd2dq(v2d) I("sse2.cvtpd2dq");
v4d cvttpd2dq(v2d) I("sse2.cvttpd2dq");
v4f cvtpd2ps(v2d) I("sse2.cvtpd2ps");
v4d cvtps2dq(v4f) I("sse2.cvtps2dq");
v4d cvttps2dq(v4f) I("sse2.cvttps2dq");
int cvtsd2si(v2d) I("sse2.cvtsd2si");
long long cvtsd2si64(v2d) I("sse2.cvtsd2si64");
int cvttsd2si(v2d) I("sse2.cvttsd2si");
long long cvttsd2si64(v2d) I("sse2.cvttsd2si64");
v4f cvtsd2ss(v4f, v2d) I("sse2.cvtsd2ss");
v16b packsswb(v8w, v8w) I("sse2.packsswb.128");
v8w packssdw(v4d, v4d) I("sse2.packssdw.128");
v16b packuswb(v8w, v8w) I("sse2.packuswb.128");
int movmsk_pd(v2d) I("sse2.movmsk.pd");
int pmovmskb(v16b) I("sse2.pmovmskb.128");
void maskmov_dqu(v16b, v16b, void *) I("sse2.maskmov.dqu");
void clflush(const void *) I("sse2.clflush");
v4f addsub_ps(v4f, v4f) I("sse3.addsub.ps");
v2d addsub_pd(v2d, v2d) I("sse3.addsub.pd");
v4f hadd_ps(v4f, v4f) I("sse3.hadd.ps");
v2d hadd_pd(v2d, v2d) I("sse3.hadd.pd");
v4f hsub_ps(v4f, v4f) I("sse3.hsub.ps");
v2d hsub_pd(v2d, v2d) I("sse3.hsub.pd");
v16b ldu_dq(const void *) I("sse3.ldu.dq");
v8w phadd_w(v8w, v8w) I("ssse3.phadd.w.128");
v4d phadd_d(v4d, v4d) I("ssse3.phadd.d.128");
v8w phadd_sw(v8w, v8w) I("ssse3.phadd.sw.128");
v8w phsub_w(v8w, v8w) I("ssse3.phsub.w.128");
v4d phsub_d(v4d, v4d) I("ssse3.phsub.d.128");
v8w phsub_sw(v8w, v8w) I("ssse3.phsub.sw.128");
v8w pmadd_ub_sw(v16b, v16b) I("ssse3.pmadd.ub.sw.128");
v8w pmul_hr_sw(v8w, v8w) I("ssse3.pmul.hr.sw.128");
v16b pshuf_b(v16b, v16b) I("ssse3.pshuf.b.128");
v16b psign_b(v16b, v16b) I("ssse3.psign.b.128");
v8w psign_w(v8w, v8w) I("ssse3.psign.w.128");
v4d psign_d(v4d, v4d) I("ssse3.psign.d.128");
v4f round_ps(v4f, int) I("sse41.round.ps");
v4f round_ss(v4f, v4f, int) I("sse41.round.ss");
v2d round_pd(v2d, int) I("sse41.round.pd");
v2d round_sd(v2d, v2d, int) I("sse41.round.sd");
v8w phminposuw(v8w) I("sse41.phminposuw");
v8w packusdw(v4d, v4d) I("sse41.packusdw");
v4f insertps(v4f, v4f, char) I("sse41.insertps");
v16b pblendvb(v16b, v16b, v16b) I("sse41.pblendvb");
v2d blendvpd(v2d, v2d, v2d) I("sse41.blendvpd");
v4f blendvps(v4f, v4f, v4f) I("sse41.blendvps");
v2d dppd(v2d, v2d, char) I("sse41.dppd");
v4f dpps(v4f, v4f, char) I("sse41.dpps");
v8w mpsadbw(v16b, v16b, char) I("sse41.mpsadbw");
int ptestz(v2q, v2q) I("sse41.ptestz");
int ptestc(v2q, v2q) I("sse41.ptestc");
int ptestnzc(v2q, v2q) I("sse41.ptestnzc");
int crc32_8(int, char) I("sse42.crc32.32.8");
int crc32_16(int, short) I("sse42.crc32.32.16");
int crc32_32(int, int) I("sse42.crc32.32.32");
long long crc32_64(long long, long long) I("sse42.crc32.64.64");
v2q pclmulqdq(v2q, v2q, char) I("pclmulqdq");
v16b pcmpistrm(v16b, v16b, char) I("sse42.pcmpistrm128");
int pcmpistri(v16b, v16b, char) I("sse42.pcmpistri128");
int pcmpistria(v16b, v16b, char) I("sse42.pcmpistria128");
int pcmpistric(v16b, v16b, char) I("sse42.pcmpistric128");
int pcmpistrio(v16b, v16b, char) I("sse42.pcmpistrio128");
int pcmpistris(v16b, v16b, char) I("sse42.pcmpistris128");
int pcmpistriz(v16b, v16b, char) I("sse42.pcmpistriz128");
v16b pcmpestrm(v16b, int, v16b, int, char) I("sse42.pcmpestrm128");
int pcmpestri(v16b, int, v16b, int, char) I("sse42.pcmpestri128");
int pcmpestria(v16b, int, v16b, int, char) I("sse42.pcmpestria128");
int pcmpestric(v16b, int, v16b, int, char) I("sse42.pcmpestric128");
int pcmpestrio(v16b, int, v16b, int, char) I("sse42.pcmpestrio128");
int pcmpestris(v16b, int, v16b, int, char) I("sse42.pcmpestris128");
int pcmpestriz(v16b, int, v16b, int, char) I("sse42.pcmpestriz128");

/* Immediates: every value, each a constant at its call site. */
#define R4(F, b) F((b)) F((b) + 1) F((b) + 2) F((b) + 3)
#define R8(F, b) R4(F, b) R4(F, (b) + 4)
#define R16(F, b) R8(F, b) R8(F, (b) + 8)
#define R64(F, b) R16(F, b) R16(F, (b) + 16) R16(F, (b) + 32) R16(F, (b) + 48)
#define R128(F, b) R64(F, b) R64(F, (b) + 64)
#define R256(F) R128(F, 0) R128(F, 128)

/* One function per intrinsic, named after it; each hashes its results over ROUNDS random inputs. */
#define TEST(name, rounds, ...) \
    static void test_##name(void) \
    { \
        begin(); \
        for (int round = 0; round < (rounds); round++) { __VA_ARGS__ } \
        end(#name); \
    }

#define FF(name) TEST(name, ROUNDS, v4f a = random_v4f(), b = random_v4f(); hash_v4f(name(a, b));)
#define DD(name) TEST(name, ROUNDS, v2d a = random_v2d(), b = random_v2d(); hash_v2d(name(a, b));)
#define FI(name) TEST(name, ROUNDS, v4f a = random_v4f(), b = random_v4f(); hash_int(name(a, b));)
#define DI(name) TEST(name, ROUNDS, v2d a = random_v2d(), b = random_v2d(); hash_int(name(a, b));)
#define WW(name) TEST(name, ROUNDS, v8w a = random_words(), b = random_words(); v8w r = name(a, b); hash_bits(&r, 16);)
#define DWD(name) TEST(name, ROUNDS, v4d a = random_dwords(), b = random_dwords(); v4d r = name(a, b); hash_bits(&r, 16);)
#define BB(name) TEST(name, ROUNDS, v16b a = random_bytes(), b = random_bytes(); v16b r = name(a, b); hash_bits(&r, 16);)

FF(min_ps) FF(min_ss) FF(max_ps) FF(max_ss) FF(addsub_ps) FF(hadd_ps) FF(hsub_ps)
DD(min_pd) DD(min_sd) DD(max_pd) DD(max_sd) DD(addsub_pd) DD(hadd_pd) DD(hsub_pd)
FI(comieq_ss) FI(comilt_ss) FI(comile_ss) FI(comigt_ss) FI(comige_ss) FI(comineq_ss)
FI(ucomieq_ss) FI(ucomilt_ss) FI(ucomile_ss) FI(ucomigt_ss) FI(ucomige_ss) FI(ucomineq_ss)
DI(comieq_sd) DI(comilt_sd) DI(comile_sd) DI(comigt_sd) DI(comige_sd) DI(comineq_sd)
DI(ucomieq_sd) DI(ucomilt_sd) DI(ucomile_sd) DI(ucomigt_sd) DI(ucomige_sd) DI(ucomineq_sd)
WW(pmulhu_w) WW(pmulh_w) WW(pavg_w) WW(phadd_w) WW(phadd_sw) WW(phsub_w) WW(phsub_sw) WW(pmul_hr_sw) WW(psign_w)
DWD(phadd_d) DWD(phsub_d) DWD(psign_d)
BB(pavg_b) BB(pshuf_b) BB(psign_b) BB(pmadd_ub_sw) BB(psad_bw)

TEST(pmadd_wd, ROUNDS, v8w a = random_words(), b = random_words(); v4d r = pmadd_wd(a, b); hash_bits(&r, 16);)
TEST(packsswb, ROUNDS, v8w a = random_words(), b = random_words(); v16b r = packsswb(a, b); hash_bits(&r, 16);)
TEST(packuswb, ROUNDS, v8w a = random_words(), b = random_words(); v16b r = packuswb(a, b); hash_bits(&r, 16);)
TEST(packssdw, ROUNDS, v4d a = random_dwords(), b = random_dwords(); v8w r = packssdw(a, b); hash_bits(&r, 16);)
TEST(packusdw, ROUNDS, v4d a = random_dwords(), b = random_dwords(); v8w r = packusdw(a, b); hash_bits(&r, 16);)
TEST(phminposuw, ROUNDS, v8w a = random_words(); if (round % 3 == 0) a[round % 8] = a[(round + 3) % 8]; v8w r = phminposuw(a); hash_bits(&r, 16);)
TEST(pblendvb, ROUNDS, v16b a = random_bytes(), b = random_bytes(), m = random_bytes(); v16b r = pblendvb(a, b, m); hash_bits(&r, 16);)
TEST(blendvps, ROUNDS, v4f a = random_v4f(), b = random_v4f(), m = random_v4f(); hash_v4f(blendvps(a, b, m));)
TEST(blendvpd, ROUNDS, v2d a = random_v2d(), b = random_v2d(), m = random_v2d(); hash_v2d(blendvpd(a, b, m));)
TEST(ptestz, ROUNDS, v2q a = random_quads(), b = round % 4 ? random_quads() : ~a; hash_int(ptestz(a, b));)
TEST(ptestc, ROUNDS, v2q a = random_quads(), b = round % 4 ? random_quads() : a & random_quads(); hash_int(ptestc(a, b));)
TEST(ptestnzc, ROUNDS, v2q a = random_quads(), b = round % 4 ? random_quads() : a; hash_int(ptestnzc(a, b));)
TEST(movmsk_ps, ROUNDS, hash_int(movmsk_ps(random_v4f()));)
TEST(movmsk_pd, ROUNDS, hash_int(movmsk_pd(random_v2d()));)
TEST(pmovmskb, ROUNDS, hash_int(pmovmskb(random_bytes()));)

#define CVT(name, input, type) TEST(name, ROUNDS, type r = name(input()); hash_bits(&r, sizeof r);)
CVT(cvtss2si, random_v4f, int) CVT(cvtss2si64, random_v4f, long long) CVT(cvttss2si, random_v4f, int) CVT(cvttss2si64, random_v4f, long long)
CVT(cvtsd2si, random_v2d, int) CVT(cvtsd2si64, random_v2d, long long) CVT(cvttsd2si, random_v2d, int) CVT(cvttsd2si64, random_v2d, long long)
CVT(cvtpd2dq, random_v2d, v4d) CVT(cvttpd2dq, random_v2d, v4d) CVT(cvtps2dq, random_v4f, v4d) CVT(cvttps2dq, random_v4f, v4d)
TEST(cvtpd2ps, ROUNDS, hash_v4f(cvtpd2ps(random_v2d()));)
TEST(cvtsd2ss, ROUNDS, v4f a = random_v4f(); hash_v4f(cvtsd2ss(a, random_v2d()));)

#define SHIFT(name, type, input) TEST(name, ROUNDS, type a = input(); v2q count = random_count(); type c; memcpy(&c, &count, 16); type r = name(a, c); hash_bits(&r, 16);)
SHIFT(psll_w, v8w, random_words) SHIFT(psll_d, v4d, random_dwords) SHIFT(psll_q, v2q, random_quads)
SHIFT(psrl_w, v8w, random_words) SHIFT(psrl_d, v4d, random_dwords) SHIFT(psrl_q, v2q, random_quads)
SHIFT(psra_w, v8w, random_words) SHIFT(psra_d, v4d, random_dwords)
#define SHIFTI(name, type, input) TEST(name, ROUNDS, type a = input(); type r = name(a, random_scalar_count()); hash_bits(&r, 16);)
SHIFTI(pslli_w, v8w, random_words) SHIFTI(pslli_d, v4d, random_dwords) SHIFTI(pslli_q, v2q, random_quads)
SHIFTI(psrli_w, v8w, random_words) SHIFTI(psrli_d, v4d, random_dwords) SHIFTI(psrli_q, v2q, random_quads)
SHIFTI(psrai_w, v8w, random_words) SHIFTI(psrai_d, v4d, random_dwords)

#define CMPPS(imm) hash_v4f(cmp_ps(a, b, (char)(imm))); hash_v4f(cmp_ss(a, b, (char)(imm)));
#define CMPPD(imm) hash_v2d(cmp_pd(c, d, (char)(imm))); hash_v2d(cmp_sd(c, d, (char)(imm)));
TEST(cmp_ps_ss, ROUNDS, v4f a = random_v4f(), b = round % 3 ? random_v4f() : a; R8(CMPPS, 0))
TEST(cmp_pd_sd, ROUNDS, v2d c = random_v2d(), d = round % 3 ? random_v2d() : c; R8(CMPPD, 0))
#define ROUNDF(imm) hash_v4f(round_ps(a, (imm))); hash_v4f(round_ss(b, a, (imm)));
#define ROUNDD(imm) hash_v2d(round_pd(c, (imm))); hash_v2d(round_sd(d, c, (imm)));
TEST(round_ps_ss, ROUNDS, v4f a = random_v4f(), b = random_v4f(); R16(ROUNDF, 0))
TEST(round_pd_sd, ROUNDS, v2d c = random_v2d(), d = random_v2d(); R16(ROUNDD, 0))
#define DPPS(imm) hash_v4f(dpps(a, b, (char)(imm)));
#define INSERTPS(imm) hash_v4f(insertps(a, b, (char)(imm)));
#define DPPD(imm) hash_v2d(dppd(c, d, (char)((imm) & 3 | ((imm) & 12) << 2)));
#define MPSADBW(imm) { v8w r = mpsadbw(x, y, (char)(imm)); hash_bits(&r, 16); }
TEST(dpps, ROUNDS / 8, v4f a = random_v4f(), b = random_v4f(); R256(DPPS))
TEST(insertps, ROUNDS / 8, v4f a = random_v4f(), b = random_v4f(); R256(INSERTPS))
TEST(dppd, ROUNDS, v2d c = random_v2d(), d = random_v2d(); R16(DPPD, 0))
TEST(mpsadbw, ROUNDS, v16b x = random_bytes(), y = random_bytes(); R8(MPSADBW, 0))

TEST(crc32, ROUNDS, long long a = (long long)next(), b = (long long)next();
     hash_int(crc32_8((int)a, (char)b)); hash_int(crc32_16((int)a, (short)b)); hash_int(crc32_32((int)a, (int)b)); hash_int(crc32_64(a, b));)

/* PCLMULQDQ is in no x86-64 level, so the native build selects it for this function. */
#define CLMUL(imm) { v2q r = pclmulqdq(a, b, (char)(imm)); hash_bits(&r, 16); }
__attribute__((target("pclmul"))) TEST(pclmulqdq, ROUNDS, v2q a = random_quads(), b = random_quads(); R4(CLMUL, 0) CLMUL(0x10) CLMUL(0x11) CLMUL(0x01) CLMUL(0xff))

/* The string comparisons, byte and word, at all 128 controls. */
#define ISTR(imm) { v16b m = pcmpistrm(a, b, (char)(imm)); hash_bits(&m, 16); } \
    hash_int(pcmpistri(a, b, (char)(imm))); hash_int(pcmpistria(a, b, (char)(imm))); hash_int(pcmpistric(a, b, (char)(imm))); \
    hash_int(pcmpistrio(a, b, (char)(imm))); hash_int(pcmpistris(a, b, (char)(imm))); hash_int(pcmpistriz(a, b, (char)(imm)));
#define ESTR(imm) { v16b m = pcmpestrm(a, la, b, lb, (char)(imm)); hash_bits(&m, 16); } \
    hash_int(pcmpestri(a, la, b, lb, (char)(imm))); hash_int(pcmpestria(a, la, b, lb, (char)(imm))); hash_int(pcmpestric(a, la, b, lb, (char)(imm))); \
    hash_int(pcmpestrio(a, la, b, lb, (char)(imm))); hash_int(pcmpestris(a, la, b, lb, (char)(imm))); hash_int(pcmpestriz(a, la, b, lb, (char)(imm)));
TEST(pcmpistr, ROUNDS / 8, v16b a = random_string(round & 1), b = random_string(round & 1); R128(ISTR, 0))
TEST(pcmpestr, ROUNDS / 8, v16b a = random_string(round & 1), b = random_string(round & 1); int la = random_length(), lb = random_length(); R128(ESTR, 0))

/* rcpps and rsqrtps: within 1.5 * 2^-12 of the exact result; zeros and denormals (counted as zeros) give infinities, and
   rcpps flushes tiny results to zero, which inputs between 2^126 * (1 - 2^-12) and 2^126 * (1 + 2^-11) may or may not do. */
static int reciprocal_error(float input, float output, int sqrt)
{
    uint32_t in, out;
    memcpy(&in, &input, 4);
    memcpy(&out, &output, 4);
    uint32_t magnitude = in & 0x7fffffff, sign = in & 0x80000000;
    if (magnitude > 0x7f800000) return output == output;
    if (sqrt && sign && magnitude >= 0x00800000) return output == output;
    if (magnitude < 0x00800000) return out != (sign | 0x7f800000);
    if (magnitude == 0x7f800000) return out != sign;
    double exact = sqrt ? 1.0 / __builtin_sqrt((double)input) : 1.0 / (double)input;
    if (!sqrt && magnitude >= 0x7e7ffd00) {
        if ((out & 0x7fffffff) == 0 && (out & 0x80000000) == sign) return 0;
        if (magnitude > 0x7e800300) return 1;
    }
    double error = ((double)output - exact) / exact;
    return error > 1.5 / 4096 || error < -1.5 / 4096;
}

static void approximation(const char *name, int sqrt, int scalar)
{
    long long errors = 0;
    for (int round = 0; round < ROUNDS; round++) {
        v4f a = random_v4f();
        v4f r = scalar ? (sqrt ? rsqrt_ss(a) : rcp_ss(a)) : (sqrt ? rsqrt_ps(a) : rcp_ps(a));
        for (int lane = 0; lane < (scalar ? 1 : 4); lane++) errors += reciprocal_error(a[lane], r[lane], sqrt);
        if (scalar && memcmp((char *)&a + 4, (char *)&r + 4, 12) != 0) errors++;
    }
    exact = portable = (uint64_t)errors;
    end(name);
}

/* The memory intrinsics: maskmovdqu writes only the selected bytes, lddqu reads at any alignment, stmxcsr reads the
   default control word, whose exception flags (bits 0 to 5) are left out because translated code does not track them. */
static void memory(void)
{
    begin();
    unsigned char buffer[48];
    for (int round = 0; round < ROUNDS; round++) {
        for (int index = 0; index < 48; index++) buffer[index] = (unsigned char)next();
        int offset = (int)(next() % 32);
        maskmov_dqu(random_bytes(), random_bytes(), buffer + offset);
        hash_bits(buffer, 48);
        v16b loaded = ldu_dq(buffer + (next() % 32));
        hash_bits(&loaded, 16);
        clflush(buffer);
    }
    unsigned control;
    stmxcsr(&control);
    hash_int(control & ~0x3fu);
    end("memory");
}

int main(int argc, char **argv)
{
    portable_only = argc > 1 && strcmp(argv[1], "portable") == 0;
    approximation("rcp_ps", 0, 0);
    approximation("rcp_ss", 0, 1);
    approximation("rsqrt_ps", 1, 0);
    approximation("rsqrt_ss", 1, 1);
    test_min_ps(); test_min_ss(); test_max_ps(); test_max_ss(); test_addsub_ps(); test_hadd_ps(); test_hsub_ps();
    test_min_pd(); test_min_sd(); test_max_pd(); test_max_sd(); test_addsub_pd(); test_hadd_pd(); test_hsub_pd();
    test_comieq_ss(); test_comilt_ss(); test_comile_ss(); test_comigt_ss(); test_comige_ss(); test_comineq_ss();
    test_ucomieq_ss(); test_ucomilt_ss(); test_ucomile_ss(); test_ucomigt_ss(); test_ucomige_ss(); test_ucomineq_ss();
    test_comieq_sd(); test_comilt_sd(); test_comile_sd(); test_comigt_sd(); test_comige_sd(); test_comineq_sd();
    test_ucomieq_sd(); test_ucomilt_sd(); test_ucomile_sd(); test_ucomigt_sd(); test_ucomige_sd(); test_ucomineq_sd();
    test_pmulhu_w(); test_pmulh_w(); test_pavg_w(); test_phadd_w(); test_phadd_sw(); test_phsub_w(); test_phsub_sw();
    test_pmul_hr_sw(); test_psign_w(); test_phadd_d(); test_phsub_d(); test_psign_d();
    test_pavg_b(); test_pshuf_b(); test_psign_b(); test_pmadd_ub_sw(); test_psad_bw();
    test_pmadd_wd(); test_packsswb(); test_packuswb(); test_packssdw(); test_packusdw(); test_phminposuw();
    test_pblendvb(); test_blendvps(); test_blendvpd(); test_ptestz(); test_ptestc(); test_ptestnzc();
    test_movmsk_ps(); test_movmsk_pd(); test_pmovmskb();
    test_cvtss2si(); test_cvtss2si64(); test_cvttss2si(); test_cvttss2si64();
    test_cvtsd2si(); test_cvtsd2si64(); test_cvttsd2si(); test_cvttsd2si64();
    test_cvtpd2dq(); test_cvttpd2dq(); test_cvtps2dq(); test_cvttps2dq(); test_cvtpd2ps(); test_cvtsd2ss();
    test_psll_w(); test_psll_d(); test_psll_q(); test_psrl_w(); test_psrl_d(); test_psrl_q(); test_psra_w(); test_psra_d();
    test_pslli_w(); test_pslli_d(); test_pslli_q(); test_psrli_w(); test_psrli_d(); test_psrli_q(); test_psrai_w(); test_psrai_d();
    test_cmp_ps_ss(); test_cmp_pd_sd(); test_round_ps_ss(); test_round_pd_sd();
    test_dpps(); test_insertps(); test_dppd(); test_mpsadbw(); test_crc32(); test_pclmulqdq();
    test_pcmpistr(); test_pcmpestr();
    memory();
    return 0;
}
