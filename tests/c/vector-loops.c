/* Loop shapes that LLVM's loop and SLP vectorizers transform. Results are printed as checksums;
 * native clang with the same flags is the oracle. The searches over fixed-size arrays are early-exit loops, which the
 * loop vectorizer leaves through llvm.experimental.cttz.elts (SV-31). */
#include <stdint.h>
#include <stdio.h>
#define N 1027
static int32_t a[N], b[N], c[N];
static int8_t narrow[N];
static int64_t wide[N];
static float f[N], g[N];
static double d[N], e[N];
struct pair { int32_t x, y; } pairs[N];
static uint32_t hash32(uint32_t value) { value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; return value ^ (value >> 16); }
static uint64_t mix(uint64_t h, uint64_t value) { return (h ^ value) * 1099511628211ull; }
__attribute__((noinline)) static void fill(void)
{
    for (int i = 0; i < N; i++) {
        a[i] = (int32_t)hash32(i); b[i] = (int32_t)hash32(i + N) % 1000; narrow[i] = (int8_t)hash32(3 * i);
        f[i] = (float)(hash32(5 * i) % 10000) / 77.0f; g[i] = (float)(i % 13) - 6.0f;
        d[i] = (double)(hash32(7 * i) % 100000) / 13.0; e[i] = 1.0 / (i + 1);
        pairs[i].x = (int32_t)hash32(11 * i) % 500; pairs[i].y = (int32_t)hash32(13 * i) % 500;
    }
}
static uint8_t haystack[300];
static int32_t flags[64];
__attribute__((noinline)) static int first_byte(uint8_t needle) { for (int i = 0; i < 300; i++) if (haystack[i] == needle) return i; return -1; }
__attribute__((noinline)) static int first_set(void) { for (int i = 0; i < 64; i++) if (flags[i]) return i; return 64; }

int main(void)
{
    fill();
    uint64_t h = 14695981039346656037ull;
    int64_t sum = 0; int32_t minimum = INT32_MAX, maximum = INT32_MIN; uint32_t product = 1;
    for (int i = 0; i < N; i++) { sum += a[i]; minimum = a[i] < minimum ? a[i] : minimum; maximum = a[i] > maximum ? a[i] : maximum; product *= (uint32_t)b[i] | 1u; }
    h = mix(h, (uint64_t)sum); h = mix(h, (uint32_t)minimum); h = mix(h, (uint32_t)maximum); h = mix(h, product);
    for (int i = 0; i < N; i++) c[i] = a[i] * 3 + (b[i] >> 2) - (a[i] & b[i]);
    for (int i = 0; i < N; i++) wide[i] = (int64_t)narrow[i] * a[i];                       /* widening */
    for (int i = 0; i < N; i++) if (b[i] > 500) c[i] = -c[i];                               /* if-conversion */
    for (int i = 0; i + 1 < N; i += 2) { c[i] += c[i + 1]; c[i + 1] ^= a[i]; }              /* interleaved */
    for (int i = 0; i < N; i++) { int t = pairs[i].x; pairs[i].x = pairs[i].y + 1; pairs[i].y = t - 1; } /* AoS */
    for (int i = N - 1; i > 0; i--) b[i] = b[i - 1] + (b[i] & 7);                           /* reverse, carried */
    int histogram[16] = {0};
    for (int i = 0; i < N; i++) histogram[(uint32_t)a[i] % 16]++;                            /* scattered updates */
    for (int i = 0; i < N; i++) h = mix(h, (uint32_t)c[i] ^ (uint64_t)wide[i] ^ (uint32_t)pairs[i].x ^ ((uint64_t)(uint32_t)b[i] << 32));
    for (int i = 0; i < 16; i++) h = mix(h, (uint32_t)histogram[i]);
    float fsum = 0, fdot = 0; double dsum = 0, ddot = 0;
    for (int i = 0; i < N; i++) { fsum += f[i]; fdot += f[i] * g[i]; }
    for (int i = 0; i < N; i++) { dsum += d[i] * e[i]; ddot = ddot * 0.5 + d[i]; }
    for (int i = 0; i < N; i++) { f[i] = f[i] * 0.5f + g[i]; d[i] = d[i] > 1000.0 ? d[i] - 1000.0 : d[i] * 2.0; }
    for (int i = 0; i < N; i++) a[i] = (int32_t)(f[i] * 16.0f) + (int32_t)d[i];               /* float to int */
    for (int i = 0; i < N; i++) h = mix(h, (uint32_t)a[i]);
    for (int i = 0; i < 300; i++) haystack[i] = (uint8_t)(hash32((uint32_t)i) | 1);
    for (int i = 0; i < 300; i += 37) h = mix(h, (uint32_t)first_byte(haystack[i]));
    h = mix(h, (uint32_t)first_byte(0));                                                   /* early exit */
    for (int k = -1; k < 64; k += 9) {
        for (int i = 0; i < 64; i++) flags[i] = i == k;
        h = mix(h, (uint32_t)first_set());
    }
    printf("checksum %016llx\n", (unsigned long long)h);
    printf("float %.6e %.6e double %.12e %.12e\n", fsum, fdot, dsum, ddot);
    return 0;
}
