/* The native test library of tests/ext-structs.sh (NX-07), built on each host with its own compiler. */
#define LLVMNET_STRUCTS_BUILD 1
#include <llvmnet-structs.h>

pairf pair_scale(pairf p, float factor) { pairf r = {p.x * factor, p.y * factor}; return r; }
float pair_length2(pairf p) { return p.x * p.x + p.y * p.y; }
mixed mixed_make(int32_t i, double d) { mixed m = {i, d}; return m; }
double mixed_sum(mixed m, int32_t scale) { return (m.i + m.d) * scale; }
odd17 odd_reverse(odd17 v) {
    odd17 r;
    for (int i = 0; i < 17; i++) r.bytes[i] = v.bytes[16 - i];
    return r;
}
uint32_t odd_sum(odd17 v) {
    uint32_t sum = 0;
    for (int i = 0; i < 17; i++) sum = sum * 31 + v.bytes[i];
    return sum;
}
quad quad_add(quad a, quad b) { quad r = {a.a + b.a, a.b + b.b, a.c + b.c, a.d + b.d}; return r; }
double quad_dot(quad a, quad b) { return a.a * b.a + a.b * b.b + a.c * b.c + a.d * b.d; }
tagged tagged_make(pairf point, int32_t tag) { tagged t = {point, tag}; return t; }
small3 small_next(small3 v) { small3 r = {(int16_t)(v.s - 1), (uint8_t)(v.c + 1)}; return r; }
int32_t structs_version(void) { return 7; }
