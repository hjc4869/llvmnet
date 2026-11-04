/* Conformance test of the x86-64-v3 target intrinsics (AVX, AVX2, FMA, F16C, BMI; llvm.x86.*, SV-09), as
   x86-sse-conformance.c is of SSE to SSE4.2, whose generators and hashes it shares: each intrinsic is declared under its
   LLVM name with an asm label and runs on random and special inputs, at every value of an immediate operand. One line
   per intrinsic: its name, a hash of every result bit and a hash with NaN results canonicalized ("portable" prints only
   that one). rcpps and rsqrtps of 256 bits print the number of results outside the instructions' documented error. */
#define main sse_conformance_main
#include "x86-sse-conformance.c"
#undef main

typedef float v8f __attribute__((vector_size(32)));
typedef double v4df __attribute__((vector_size(32)));
typedef long long v4q __attribute__((vector_size(32)));
typedef int v8i __attribute__((vector_size(32)));
typedef short v16w __attribute__((vector_size(32)));
typedef char v32b __attribute__((vector_size(32)));

#define JOIN(type, low, high) ({ type joined; __typeof__(low) parts[2] = {(low), (high)}; memcpy(&joined, parts, 32); joined; })
static v8f random_v8f(void) { return JOIN(v8f, random_v4f(), random_v4f()); }
static v4df random_v4df(void) { return JOIN(v4df, random_v2d(), random_v2d()); }
static v32b random_bytes32(void) { return JOIN(v32b, random_bytes(), random_bytes()); }
static v16w random_words16(void) { return JOIN(v16w, random_words(), random_words()); }
static v8i random_dwords8(void) { return JOIN(v8i, random_dwords(), random_dwords()); }
static v4q random_quads4(void) { return JOIN(v4q, random_quads(), random_quads()); }
static void hash_v8f(v8f value) { v4f halves[2]; memcpy(halves, &value, 32); hash_v4f(halves[0]); hash_v4f(halves[1]); }
static void hash_v4df(v4df value) { v2d halves[2]; memcpy(halves, &value, 32); hash_v2d(halves[0]); hash_v2d(halves[1]); }

/* Per-element shift counts: mostly within and around the element's width, sometimes any value. */
static v8i random_counts32(void)
{
    v8i value;
    for (int lane = 0; lane < 8; lane++) {
        uint64_t choice = next();
        value[lane] = choice % 4 == 0 ? (int)(uint32_t)next() : (int)((choice >> 8) % 40);
    }
    return value;
}

static v4q random_counts64(void)
{
    v4q value;
    for (int lane = 0; lane < 4; lane++) {
        uint64_t choice = next();
        value[lane] = choice % 4 == 0 ? (long long)next() : (long long)((choice >> 8) % 72);
    }
    return value;
}

/* ---- AVX ---- */
v8f addsub_ps256(v8f, v8f) I("avx.addsub.ps.256");
v4df addsub_pd256(v4df, v4df) I("avx.addsub.pd.256");
v8f blendv_ps256(v8f, v8f, v8f) I("avx.blendv.ps.256");
v4df blendv_pd256(v4df, v4df, v4df) I("avx.blendv.pd.256");
v4f cvt_pd2_ps256(v4df) I("avx.cvt.pd2.ps.256");
v4d cvt_pd2dq256(v4df) I("avx.cvt.pd2dq.256");
v4d cvtt_pd2dq256(v4df) I("avx.cvtt.pd2dq.256");
v8i cvt_ps2dq256(v8f) I("avx.cvt.ps2dq.256");
v8i cvtt_ps2dq256(v8f) I("avx.cvtt.ps2dq.256");
v8f hadd_ps256(v8f, v8f) I("avx.hadd.ps.256");
v4df hadd_pd256(v4df, v4df) I("avx.hadd.pd.256");
v8f hsub_ps256(v8f, v8f) I("avx.hsub.ps.256");
v4df hsub_pd256(v4df, v4df) I("avx.hsub.pd.256");
v8f max_ps256(v8f, v8f) I("avx.max.ps.256");
v4df max_pd256(v4df, v4df) I("avx.max.pd.256");
v8f min_ps256(v8f, v8f) I("avx.min.ps.256");
v4df min_pd256(v4df, v4df) I("avx.min.pd.256");
v8f rcp_ps256(v8f) I("avx.rcp.ps.256");
v8f rsqrt_ps256(v8f) I("avx.rsqrt.ps.256");
v8f round_ps256(v8f, int) I("avx.round.ps.256");
v4df round_pd256(v4df, int) I("avx.round.pd.256");
v8f dp_ps256(v8f, v8f, char) I("avx.dp.ps.256");
int movmsk_ps256(v8f) I("avx.movmsk.ps.256");
int movmsk_pd256(v4df) I("avx.movmsk.pd.256");
v32b ldu_dq256(const void *) I("avx.ldu.dq.256");
void vzeroupper(void) I("avx.vzeroupper");
void vzeroall(void) I("avx.vzeroall");
v4f vpermilvar_ps(v4f, v4d) I("avx.vpermilvar.ps");
v8f vpermilvar_ps256(v8f, v8i) I("avx.vpermilvar.ps.256");
v2d vpermilvar_pd(v2d, v2q) I("avx.vpermilvar.pd");
v4df vpermilvar_pd256(v4df, v4q) I("avx.vpermilvar.pd.256");
int ptestz256(v4q, v4q) I("avx.ptestz.256");
int ptestc256(v4q, v4q) I("avx.ptestc.256");
int ptestnzc256(v4q, v4q) I("avx.ptestnzc.256");
int vtestz_ps(v4f, v4f) I("avx.vtestz.ps");
int vtestc_ps(v4f, v4f) I("avx.vtestc.ps");
int vtestnzc_ps(v4f, v4f) I("avx.vtestnzc.ps");
int vtestz_pd(v2d, v2d) I("avx.vtestz.pd");
int vtestc_pd(v2d, v2d) I("avx.vtestc.pd");
int vtestnzc_pd(v2d, v2d) I("avx.vtestnzc.pd");
int vtestz_ps256(v8f, v8f) I("avx.vtestz.ps.256");
int vtestc_ps256(v8f, v8f) I("avx.vtestc.ps.256");
int vtestnzc_ps256(v8f, v8f) I("avx.vtestnzc.ps.256");
int vtestz_pd256(v4df, v4df) I("avx.vtestz.pd.256");
int vtestc_pd256(v4df, v4df) I("avx.vtestc.pd.256");
int vtestnzc_pd256(v4df, v4df) I("avx.vtestnzc.pd.256");
v4f maskload_ps(const void *, v4d) I("avx.maskload.ps");
v8f maskload_ps256(const void *, v8i) I("avx.maskload.ps.256");
v2d maskload_pd(const void *, v2q) I("avx.maskload.pd");
v4df maskload_pd256(const void *, v4q) I("avx.maskload.pd.256");
void maskstore_ps(void *, v4d, v4f) I("avx.maskstore.ps");
void maskstore_ps256(void *, v8i, v8f) I("avx.maskstore.ps.256");
void maskstore_pd(void *, v2q, v2d) I("avx.maskstore.pd");
void maskstore_pd256(void *, v4q, v4df) I("avx.maskstore.pd.256");

/* ---- AVX2 ---- */
v4d maskload_d(const void *, v4d) I("avx2.maskload.d");
v8i maskload_d256(const void *, v8i) I("avx2.maskload.d.256");
v2q maskload_q(const void *, v2q) I("avx2.maskload.q");
v4q maskload_q256(const void *, v4q) I("avx2.maskload.q.256");
void maskstore_d(void *, v4d, v4d) I("avx2.maskstore.d");
void maskstore_d256(void *, v8i, v8i) I("avx2.maskstore.d.256");
void maskstore_q(void *, v2q, v2q) I("avx2.maskstore.q");
void maskstore_q256(void *, v4q, v4q) I("avx2.maskstore.q.256");
v16w packssdw256(v8i, v8i) I("avx2.packssdw");
v32b packsswb256(v16w, v16w) I("avx2.packsswb");
v16w packusdw256(v8i, v8i) I("avx2.packusdw");
v32b packuswb256(v16w, v16w) I("avx2.packuswb");
v32b pavg_b256(v32b, v32b) I("avx2.pavg.b");
v16w pavg_w256(v16w, v16w) I("avx2.pavg.w");
v32b pblendvb256(v32b, v32b, v32b) I("avx2.pblendvb");
v8i permd(v8i, v8i) I("avx2.permd");
v8f permps(v8f, v8i) I("avx2.permps");
v16w phadd_w256(v16w, v16w) I("avx2.phadd.w");
v8i phadd_d256(v8i, v8i) I("avx2.phadd.d");
v16w phadd_sw256(v16w, v16w) I("avx2.phadd.sw");
v16w phsub_w256(v16w, v16w) I("avx2.phsub.w");
v8i phsub_d256(v8i, v8i) I("avx2.phsub.d");
v16w phsub_sw256(v16w, v16w) I("avx2.phsub.sw");
v16w pmadd_ub_sw256(v32b, v32b) I("avx2.pmadd.ub.sw");
v8i pmadd_wd256(v16w, v16w) I("avx2.pmadd.wd");
v16w pmul_hr_sw256(v16w, v16w) I("avx2.pmul.hr.sw");
v16w pmulh_w256(v16w, v16w) I("avx2.pmulh.w");
v16w pmulhu_w256(v16w, v16w) I("avx2.pmulhu.w");
int pmovmskb256(v32b) I("avx2.pmovmskb");
v4q psad_bw256(v32b, v32b) I("avx2.psad.bw");
v32b pshuf_b256(v32b, v32b) I("avx2.pshuf.b");
v32b psign_b256(v32b, v32b) I("avx2.psign.b");
v16w psign_w256(v16w, v16w) I("avx2.psign.w");
v8i psign_d256(v8i, v8i) I("avx2.psign.d");
v16w mpsadbw256(v32b, v32b, char) I("avx2.mpsadbw");
v16w psll_w256(v16w, v8w) I("avx2.psll.w");
v8i psll_d256(v8i, v4d) I("avx2.psll.d");
v4q psll_q256(v4q, v2q) I("avx2.psll.q");
v16w psrl_w256(v16w, v8w) I("avx2.psrl.w");
v8i psrl_d256(v8i, v4d) I("avx2.psrl.d");
v4q psrl_q256(v4q, v2q) I("avx2.psrl.q");
v16w psra_w256(v16w, v8w) I("avx2.psra.w");
v8i psra_d256(v8i, v4d) I("avx2.psra.d");
v16w pslli_w256(v16w, int) I("avx2.pslli.w");
v8i pslli_d256(v8i, int) I("avx2.pslli.d");
v4q pslli_q256(v4q, int) I("avx2.pslli.q");
v16w psrli_w256(v16w, int) I("avx2.psrli.w");
v8i psrli_d256(v8i, int) I("avx2.psrli.d");
v4q psrli_q256(v4q, int) I("avx2.psrli.q");
v16w psrai_w256(v16w, int) I("avx2.psrai.w");
v8i psrai_d256(v8i, int) I("avx2.psrai.d");
v4d psllv_d(v4d, v4d) I("avx2.psllv.d");
v8i psllv_d256(v8i, v8i) I("avx2.psllv.d.256");
v2q psllv_q(v2q, v2q) I("avx2.psllv.q");
v4q psllv_q256(v4q, v4q) I("avx2.psllv.q.256");
v4d psrlv_d(v4d, v4d) I("avx2.psrlv.d");
v8i psrlv_d256(v8i, v8i) I("avx2.psrlv.d.256");
v2q psrlv_q(v2q, v2q) I("avx2.psrlv.q");
v4q psrlv_q256(v4q, v4q) I("avx2.psrlv.q.256");
v4d psrav_d(v4d, v4d) I("avx2.psrav.d");
v8i psrav_d256(v8i, v8i) I("avx2.psrav.d.256");
v4d gather_d_d(v4d, const void *, v4d, v4d, char) I("avx2.gather.d.d");
v8i gather_d_d256(v8i, const void *, v8i, v8i, char) I("avx2.gather.d.d.256");
v4f gather_d_ps(v4f, const void *, v4d, v4f, char) I("avx2.gather.d.ps");
v8f gather_d_ps256(v8f, const void *, v8i, v8f, char) I("avx2.gather.d.ps.256");
v2q gather_d_q(v2q, const void *, v4d, v2q, char) I("avx2.gather.d.q");
v4q gather_d_q256(v4q, const void *, v4d, v4q, char) I("avx2.gather.d.q.256");
v2d gather_d_pd(v2d, const void *, v4d, v2d, char) I("avx2.gather.d.pd");
v4df gather_d_pd256(v4df, const void *, v4d, v4df, char) I("avx2.gather.d.pd.256");
v2q gather_q_q(v2q, const void *, v2q, v2q, char) I("avx2.gather.q.q");
v4q gather_q_q256(v4q, const void *, v4q, v4q, char) I("avx2.gather.q.q.256");
v2d gather_q_pd(v2d, const void *, v2q, v2d, char) I("avx2.gather.q.pd");
v4df gather_q_pd256(v4df, const void *, v4q, v4df, char) I("avx2.gather.q.pd.256");
v4d gather_q_d(v4d, const void *, v2q, v4d, char) I("avx2.gather.q.d");
v4d gather_q_d256(v4d, const void *, v4q, v4d, char) I("avx2.gather.q.d.256");
v4f gather_q_ps(v4f, const void *, v2q, v4f, char) I("avx2.gather.q.ps");
v4f gather_q_ps256(v4f, const void *, v4q, v4f, char) I("avx2.gather.q.ps.256");

/* ---- FMA, F16C, BMI ---- */
v4f vfmaddsub_ps(v4f, v4f, v4f) I("fma.vfmaddsub.ps");
v2d vfmaddsub_pd(v2d, v2d, v2d) I("fma.vfmaddsub.pd");
v8f vfmaddsub_ps256(v8f, v8f, v8f) I("fma.vfmaddsub.ps.256");
v4df vfmaddsub_pd256(v4df, v4df, v4df) I("fma.vfmaddsub.pd.256");
v8w vcvtps2ph(v4f, int) I("vcvtps2ph.128");
v8w vcvtps2ph256(v8f, int) I("vcvtps2ph.256");
int bextr32(int, int) I("bmi.bextr.32");
long long bextr64(long long, long long) I("bmi.bextr.64");
int bzhi32(int, int) I("bmi.bzhi.32");
long long bzhi64(long long, long long) I("bmi.bzhi.64");
int pdep32(int, int) I("bmi.pdep.32");
long long pdep64(long long, long long) I("bmi.pdep.64");
int pext32(int, int) I("bmi.pext.32");
long long pext64(long long, long long) I("bmi.pext.64");

#define F8(name) TEST(name, ROUNDS, v8f a = random_v8f(), b = random_v8f(); hash_v8f(name(a, b));)
#define D4(name) TEST(name, ROUNDS, v4df a = random_v4df(), b = random_v4df(); hash_v4df(name(a, b));)
#define W16(name) TEST(name, ROUNDS, v16w a = random_words16(), b = random_words16(); v16w r = name(a, b); hash_bits(&r, 32);)
#define D8(name) TEST(name, ROUNDS, v8i a = random_dwords8(), b = random_dwords8(); v8i r = name(a, b); hash_bits(&r, 32);)
#define B32(name) TEST(name, ROUNDS, v32b a = random_bytes32(), b = random_bytes32(); v32b r = name(a, b); hash_bits(&r, 32);)
F8(addsub_ps256) F8(hadd_ps256) F8(hsub_ps256) F8(max_ps256) F8(min_ps256)
D4(addsub_pd256) D4(hadd_pd256) D4(hsub_pd256) D4(max_pd256) D4(min_pd256)
W16(pavg_w256) W16(phadd_w256) W16(phadd_sw256) W16(phsub_w256) W16(phsub_sw256) W16(pmul_hr_sw256) W16(pmulh_w256)
W16(pmulhu_w256) W16(psign_w256)
D8(phadd_d256) D8(phsub_d256) D8(psign_d256) D8(permd)
B32(pavg_b256) B32(pshuf_b256) B32(psign_b256)
TEST(packssdw256, ROUNDS, v8i a = random_dwords8(), b = random_dwords8(); v16w r = packssdw256(a, b); hash_bits(&r, 32);)
TEST(packusdw256, ROUNDS, v8i a = random_dwords8(), b = random_dwords8(); v16w r = packusdw256(a, b); hash_bits(&r, 32);)
TEST(packsswb256, ROUNDS, v16w a = random_words16(), b = random_words16(); v32b r = packsswb256(a, b); hash_bits(&r, 32);)
TEST(packuswb256, ROUNDS, v16w a = random_words16(), b = random_words16(); v32b r = packuswb256(a, b); hash_bits(&r, 32);)
TEST(pmadd_ub_sw256, ROUNDS, v32b a = random_bytes32(), b = random_bytes32(); v16w r = pmadd_ub_sw256(a, b); hash_bits(&r, 32);)
TEST(pmadd_wd256, ROUNDS, v16w a = random_words16(), b = random_words16(); v8i r = pmadd_wd256(a, b); hash_bits(&r, 32);)
TEST(psad_bw256, ROUNDS, v32b a = random_bytes32(), b = random_bytes32(); v4q r = psad_bw256(a, b); hash_bits(&r, 32);)
TEST(pblendvb256, ROUNDS, v32b a = random_bytes32(), b = random_bytes32(), m = random_bytes32(); v32b r = pblendvb256(a, b, m); hash_bits(&r, 32);)
TEST(blendv_ps256, ROUNDS, v8f a = random_v8f(), b = random_v8f(), m = random_v8f(); hash_v8f(blendv_ps256(a, b, m));)
TEST(blendv_pd256, ROUNDS, v4df a = random_v4df(), b = random_v4df(), m = random_v4df(); hash_v4df(blendv_pd256(a, b, m));)
TEST(permps, ROUNDS, v8f a = random_v8f(); v8i c = random_dwords8(); hash_v8f(permps(a, c));)
TEST(pmovmskb256, ROUNDS, hash_int(pmovmskb256(random_bytes32()));)
TEST(movmsk_ps256, ROUNDS, hash_int(movmsk_ps256(random_v8f()));)
TEST(movmsk_pd256, ROUNDS, hash_int(movmsk_pd256(random_v4df()));)
TEST(cvt_pd2_ps256, ROUNDS, hash_v4f(cvt_pd2_ps256(random_v4df()));)
TEST(cvt_pd2dq256, ROUNDS, v4d r = cvt_pd2dq256(random_v4df()); hash_bits(&r, 16);)
TEST(cvtt_pd2dq256, ROUNDS, v4d r = cvtt_pd2dq256(random_v4df()); hash_bits(&r, 16);)
TEST(cvt_ps2dq256, ROUNDS, v8i r = cvt_ps2dq256(random_v8f()); hash_bits(&r, 32);)
TEST(cvtt_ps2dq256, ROUNDS, v8i r = cvtt_ps2dq256(random_v8f()); hash_bits(&r, 32);)
TEST(vpermilvar_ps, ROUNDS, v4f a = random_v4f(); hash_v4f(vpermilvar_ps(a, random_dwords()));)
TEST(vpermilvar_ps256, ROUNDS, v8f a = random_v8f(); hash_v8f(vpermilvar_ps256(a, random_dwords8()));)
TEST(vpermilvar_pd, ROUNDS, v2d a = random_v2d(); hash_v2d(vpermilvar_pd(a, random_quads()));)
TEST(vpermilvar_pd256, ROUNDS, v4df a = random_v4df(); hash_v4df(vpermilvar_pd256(a, random_quads4()));)
TEST(ptest256, ROUNDS, v4q a = random_quads4(), b = round % 4 ? random_quads4() : round % 8 ? ~a : a;
     hash_int(ptestz256(a, b)); hash_int(ptestc256(a, b)); hash_int(ptestnzc256(a, b));)
TEST(vtest_ps, ROUNDS, v4f a = random_v4f(), b = round % 4 ? random_v4f() : -a;
     hash_int(vtestz_ps(a, b)); hash_int(vtestc_ps(a, b)); hash_int(vtestnzc_ps(a, b));)
TEST(vtest_pd, ROUNDS, v2d a = random_v2d(), b = round % 4 ? random_v2d() : -a;
     hash_int(vtestz_pd(a, b)); hash_int(vtestc_pd(a, b)); hash_int(vtestnzc_pd(a, b));)
TEST(vtest_ps256, ROUNDS, v8f a = random_v8f(), b = round % 4 ? random_v8f() : -a;
     hash_int(vtestz_ps256(a, b)); hash_int(vtestc_ps256(a, b)); hash_int(vtestnzc_ps256(a, b));)
TEST(vtest_pd256, ROUNDS, v4df a = random_v4df(), b = round % 4 ? random_v4df() : -a;
     hash_int(vtestz_pd256(a, b)); hash_int(vtestc_pd256(a, b)); hash_int(vtestnzc_pd256(a, b));)

#define SHIFT256(name, type, input, count_type) TEST(name, ROUNDS, type a = input(); v2q count = random_count(); count_type c; \
    memcpy(&c, &count, 16); type r = name(a, c); hash_bits(&r, 32);)
SHIFT256(psll_w256, v16w, random_words16, v8w) SHIFT256(psll_d256, v8i, random_dwords8, v4d) SHIFT256(psll_q256, v4q, random_quads4, v2q)
SHIFT256(psrl_w256, v16w, random_words16, v8w) SHIFT256(psrl_d256, v8i, random_dwords8, v4d) SHIFT256(psrl_q256, v4q, random_quads4, v2q)
SHIFT256(psra_w256, v16w, random_words16, v8w) SHIFT256(psra_d256, v8i, random_dwords8, v4d)
#define SHIFTI256(name, type, input) TEST(name, ROUNDS, type a = input(); type r = name(a, random_scalar_count()); hash_bits(&r, 32);)
SHIFTI256(pslli_w256, v16w, random_words16) SHIFTI256(pslli_d256, v8i, random_dwords8) SHIFTI256(pslli_q256, v4q, random_quads4)
SHIFTI256(psrli_w256, v16w, random_words16) SHIFTI256(psrli_d256, v8i, random_dwords8) SHIFTI256(psrli_q256, v4q, random_quads4)
SHIFTI256(psrai_w256, v16w, random_words16) SHIFTI256(psrai_d256, v8i, random_dwords8)
TEST(variable_shifts, ROUNDS, v8i a = random_dwords8(), c = random_counts32(); v4q q = random_quads4(), d = random_counts64();
     v4d a4, c4; v2q q2, d2; memcpy(&a4, &a, 16); memcpy(&c4, &c, 16); memcpy(&q2, &q, 16); memcpy(&d2, &d, 16);
     v8i r8[3] = {psllv_d256(a, c), psrlv_d256(a, c), psrav_d256(a, c)}; hash_bits(r8, sizeof r8);
     v4d r4[3] = {psllv_d(a4, c4), psrlv_d(a4, c4), psrav_d(a4, c4)}; hash_bits(r4, sizeof r4);
     v4q rq[2] = {psllv_q256(q, d), psrlv_q256(q, d)}; hash_bits(rq, sizeof rq);
     v2q r2[2] = {psllv_q(q2, d2), psrlv_q(q2, d2)}; hash_bits(r2, sizeof r2);)

#define ROUND8(imm) hash_v8f(round_ps256(a, (imm))); hash_v4df(round_pd256(c, (imm)));
TEST(round256, ROUNDS, v8f a = random_v8f(); v4df c = random_v4df(); R16(ROUND8, 0))
#define DPPS256(imm) hash_v8f(dp_ps256(a, b, (char)(imm)));
TEST(dp_ps256, ROUNDS / 8, v8f a = random_v8f(), b = random_v8f(); R256(DPPS256))
#define MPSADBW256(imm) { v16w r = mpsadbw256(x, y, (char)(imm)); hash_bits(&r, 32); }
TEST(mpsadbw256, ROUNDS / 4, v32b x = random_bytes32(), y = random_bytes32(); R64(MPSADBW256, 0))
#define F16(imm) { v8w r = vcvtps2ph(a, (imm)); hash_bits(&r, 16); v8w s = vcvtps2ph256(b, (imm)); hash_bits(&s, 16); }
TEST(vcvtps2ph, ROUNDS, v4f a = random_v4f(); v8f b = random_v8f(); R16(F16, 0))

TEST(fmaddsub, ROUNDS, v4f a = random_v4f(), b = random_v4f(), c = random_v4f(); v2d d = random_v2d(), e = random_v2d(), f = random_v2d();
     hash_v4f(vfmaddsub_ps(a, b, c)); hash_v2d(vfmaddsub_pd(d, e, f));
     v8f g = random_v8f(), h = random_v8f(), i = random_v8f(); v4df j = random_v4df(), k = random_v4df(), l = random_v4df();
     hash_v8f(vfmaddsub_ps256(g, h, i)); hash_v4df(vfmaddsub_pd256(j, k, l));)

TEST(bmi, ROUNDS, long long a = (long long)next(), b = (long long)next();
     int control = (int)(next() % 4 == 0 ? next() : (next() % 72) | (next() % 72) << 8);
     hash_int(bextr32((int)a, control)); hash_int(bextr64(a, control)); hash_int(bextr64(a, b));
     hash_int(bzhi32((int)a, control)); hash_int(bzhi64(a, control)); hash_int(bzhi64(a, b));
     hash_int(pdep32((int)a, (int)b)); hash_int(pdep64(a, b)); hash_int(pext32((int)a, (int)b)); hash_int(pext64(a, b));)

static long long reciprocal_errors(int sqrt)
{
    long long errors = 0;
    for (int round = 0; round < ROUNDS; round++) {
        v8f a = random_v8f();
        v8f r = sqrt ? rsqrt_ps256(a) : rcp_ps256(a);
        for (int lane = 0; lane < 8; lane++) errors += reciprocal_error(a[lane], r[lane], sqrt);
    }
    return errors;
}

/* Masked loads and stores touch only the selected elements, at any alignment; gathers read base + index * scale where
   the mask's sign bit is set and keep the source element elsewhere, with indices of either sign. */
static void memory256(void)
{
    begin();
    _Alignas(32) unsigned char buffer[160];
    for (int round = 0; round < ROUNDS; round++) {
        for (int index = 0; index < 160; index++) buffer[index] = (unsigned char)next();
        unsigned char *at = buffer + 32 + next() % 64;
        v8i m8 = random_dwords8();
        v4q m4 = random_quads4();
        v4d m4d;
        v2q m2;
        memcpy(&m4d, &m8, 16);
        memcpy(&m2, &m4, 16);
        hash_v4f(maskload_ps(at, m4d)); hash_v8f(maskload_ps256(at, m8)); hash_v2d(maskload_pd(at, m2)); hash_v4df(maskload_pd256(at, m4));
        { v4d r = maskload_d(at, m4d); hash_bits(&r, 16); } { v8i r = maskload_d256(at, m8); hash_bits(&r, 32); }
        { v2q r = maskload_q(at, m2); hash_bits(&r, 16); } { v4q r = maskload_q256(at, m4); hash_bits(&r, 32); }
        switch (round % 8) {
        case 0: maskstore_ps(at, m4d, random_v4f()); break;
        case 1: maskstore_ps256(at, m8, random_v8f()); break;
        case 2: maskstore_pd(at, m2, random_v2d()); break;
        case 3: maskstore_pd256(at, m4, random_v4df()); break;
        case 4: maskstore_d(at, m4d, random_dwords()); break;
        case 5: maskstore_d256(at, m8, random_dwords8()); break;
        case 6: maskstore_q(at, m2, random_quads()); break;
        default: maskstore_q256(at, m4, random_quads4()); break;
        }
        hash_bits(buffer, 160);
        v32b loaded = ldu_dq256(buffer + next() % 128);
        hash_bits(&loaded, 32);
        v8i index = random_dwords8() % 9;
        v4q qindex = random_quads4() % 9;
        v4d index4;
        v2q qindex2;
        memcpy(&index4, &index, 16);
        memcpy(&qindex2, &qindex, 16);
        const unsigned char *base = buffer + 80;
#define GATHER(scale) \
        { v4d r = gather_d_d(random_dwords(), base, index4, m4d, scale); hash_bits(&r, 16); } \
        { v8i r = gather_d_d256(random_dwords8(), base, index, m8, scale); hash_bits(&r, 32); } \
        { v4f r = gather_d_ps(random_v4f(), base, index4, (v4f)m4d, scale); hash_bits(&r, 16); } \
        { v8f r = gather_d_ps256(random_v8f(), base, index, (v8f)m8, scale); hash_bits(&r, 32); } \
        { v2q r = gather_d_q(random_quads(), base, index4, m2, scale); hash_bits(&r, 16); } \
        { v4q r = gather_d_q256(random_quads4(), base, index4, m4, scale); hash_bits(&r, 32); } \
        { v2d r = gather_d_pd(random_v2d(), base, index4, (v2d)m2, scale); hash_bits(&r, 16); } \
        { v4df r = gather_d_pd256(random_v4df(), base, index4, (v4df)m4, scale); hash_bits(&r, 32); } \
        { v2q r = gather_q_q(random_quads(), base, qindex2, m2, scale); hash_bits(&r, 16); } \
        { v4q r = gather_q_q256(random_quads4(), base, qindex, m4, scale); hash_bits(&r, 32); } \
        { v2d r = gather_q_pd(random_v2d(), base, qindex2, (v2d)m2, scale); hash_bits(&r, 16); } \
        { v4df r = gather_q_pd256(random_v4df(), base, qindex, (v4df)m4, scale); hash_bits(&r, 32); } \
        { v4d r = gather_q_d(random_dwords(), base, qindex2, m4d, scale); hash_bits(&r, 16); } \
        { v4d r = gather_q_d256(random_dwords(), base, qindex, m4d, scale); hash_bits(&r, 16); } \
        { v4f r = gather_q_ps(random_v4f(), base, qindex2, (v4f)m4d, scale); hash_bits(&r, 16); } \
        { v4f r = gather_q_ps256(random_v4f(), base, qindex, (v4f)m4d, scale); hash_bits(&r, 16); }
        GATHER(1) GATHER(2) GATHER(4) GATHER(8)
        vzeroupper();
        vzeroall();
    }
    end("memory256");
}

int main(int argc, char **argv)
{
    portable_only = argc > 1 && strcmp(argv[1], "portable") == 0;
    exact = portable = (uint64_t)reciprocal_errors(0);
    end("rcp_ps256");
    exact = portable = (uint64_t)reciprocal_errors(1);
    end("rsqrt_ps256");
    test_addsub_ps256(); test_hadd_ps256(); test_hsub_ps256(); test_max_ps256(); test_min_ps256();
    test_addsub_pd256(); test_hadd_pd256(); test_hsub_pd256(); test_max_pd256(); test_min_pd256();
    test_pavg_w256(); test_phadd_w256(); test_phadd_sw256(); test_phsub_w256(); test_phsub_sw256(); test_pmul_hr_sw256();
    test_pmulh_w256(); test_pmulhu_w256(); test_psign_w256();
    test_phadd_d256(); test_phsub_d256(); test_psign_d256(); test_permd();
    test_pavg_b256(); test_pshuf_b256(); test_psign_b256();
    test_packssdw256(); test_packusdw256(); test_packsswb256(); test_packuswb256(); test_pmadd_ub_sw256(); test_pmadd_wd256();
    test_psad_bw256(); test_pblendvb256(); test_blendv_ps256(); test_blendv_pd256(); test_permps();
    test_pmovmskb256(); test_movmsk_ps256(); test_movmsk_pd256();
    test_cvt_pd2_ps256(); test_cvt_pd2dq256(); test_cvtt_pd2dq256(); test_cvt_ps2dq256(); test_cvtt_ps2dq256();
    test_vpermilvar_ps(); test_vpermilvar_ps256(); test_vpermilvar_pd(); test_vpermilvar_pd256();
    test_ptest256(); test_vtest_ps(); test_vtest_pd(); test_vtest_ps256(); test_vtest_pd256();
    test_psll_w256(); test_psll_d256(); test_psll_q256(); test_psrl_w256(); test_psrl_d256(); test_psrl_q256();
    test_psra_w256(); test_psra_d256();
    test_pslli_w256(); test_pslli_d256(); test_pslli_q256(); test_psrli_w256(); test_psrli_d256(); test_psrli_q256();
    test_psrai_w256(); test_psrai_d256(); test_variable_shifts();
    test_round256(); test_dp_ps256(); test_mpsadbw256(); test_vcvtps2ph(); test_fmaddsub(); test_bmi();
    memory256();
    return 0;
}
