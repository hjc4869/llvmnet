/* A program with assembly kernels and C fallbacks, as codecs are: it asks whether this host has the package's library
   and calls the kernels or its C functions through a table, then calls the kernels directly. The output does not depend
   on which ran. With the argument "kernels" it calls the kernels even without a library, which must end the process
   with llvmnet's message. The kernels share data with the program both ways: sk_weighted reads a constant table and a
   variable the program defines, and the program reads and writes sk_counts, which the package defines. */
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#ifdef __LLVMNET__
#include <llvmnet/cpu.h>
#else
static int llvmnet_native_kernels(const char *name) { (void)name; return 1; }
#endif

void sk_add_i32(int32_t *dst, const int32_t *a, const int32_t *b, size_t n);
double sk_dot_f64(const double *a, const double *b, size_t n);
int16_t sk_mix(int8_t x, uint16_t y, int64_t z, float w);
int64_t sk_weighted(const int16_t *x);
void sk_count(int32_t index);

const int16_t sk_weights[8] = {3, -1, 4, -1, 5, -9, 2, 6};
int32_t sk_scale;
extern int32_t sk_counts[4];
static int32_t c_counts[4] = {10, 20, 30, 40};

static void c_add_i32(int32_t *dst, const int32_t *a, const int32_t *b, size_t n)
{
    for (size_t index = 0; index < n; index++) dst[index] = (int32_t)((uint32_t)a[index] + (uint32_t)b[index]);
}

static double c_dot_f64(const double *a, const double *b, size_t n)
{
    double sum = 0;
    for (size_t index = 0; index < n; index++) sum += a[index] * b[index];
    return sum;
}

static int16_t c_mix(int8_t x, uint16_t y, int64_t z, float w) { return (int16_t)(x + y + z + (int64_t)w); }

static int64_t c_weighted(const int16_t *x)
{
    int64_t sum = 0;
    for (int index = 0; index < 8; index++) sum += (int64_t)x[index] * sk_weights[index];
    return sum * sk_scale;
}

static void c_count(int32_t index) { c_counts[index & 3]++; }

struct dsp {
    void (*add)(int32_t *, const int32_t *, const int32_t *, size_t);
    double (*dot)(const double *, const double *, size_t);
    int16_t (*mix)(int8_t, uint16_t, int64_t, float);
    int64_t (*weighted)(const int16_t *);
    void (*count)(int32_t);
    int32_t *counts;
};

int main(int argc, char **argv)
{
    int kernels = llvmnet_native_kernels("synthetic") || (argc > 1 && strcmp(argv[1], "kernels") == 0);
    struct dsp dsp = kernels ? (struct dsp){sk_add_i32, sk_dot_f64, sk_mix, sk_weighted, sk_count, sk_counts}
                              : (struct dsp){c_add_i32, c_dot_f64, c_mix, c_weighted, c_count, c_counts};
    int32_t a[103], b[103], sum[103];
    double x[50], y[50];
    for (int index = 0; index < 103; index++) {
        a[index] = index * 7919 - 400000;
        b[index] = index % 3 == 0 ? 2147483647 : -index * 31;
    }
    for (int index = 0; index < 50; index++) {
        x[index] = index * 0.1 - 2.5;
        y[index] = 1.0 / (index + 1);
    }
    dsp.add(sum, a, b, 103);
    uint32_t hash = 2166136261u;
    for (int index = 0; index < 103; index++) hash = (hash ^ (uint32_t)sum[index]) * 16777619u;
    printf("add: %08x\n", hash);
    printf("dot: %.17g\n", dsp.dot(x, y, 50));
    printf("mix: %d %d %d\n", dsp.mix(-100, 65000, -70000, 2.75f), dsp.mix(127, 0, 32000, -1.5f), dsp.mix(-1, 1, 0, 0.0f));
    volatile int32_t scale = -3;
    sk_scale = scale;
    int16_t samples[8] = {1000, -2000, 3000, 32767, -32768, 7, 0, 12345};
    printf("weighted: %lld %d\n", (long long)dsp.weighted(samples), sk_weights[5]);
    for (int index = 0; index < 11; index++) dsp.count(index * 3);
    dsp.counts[2] += 100;
    dsp.count(2);
    printf("counts: %d %d %d %d\n", dsp.counts[0], dsp.counts[1], dsp.counts[2], dsp.counts[3]);
    if (kernels) {
        sk_add_i32(sum, b, a, 7);
        printf("direct: %d %.17g %d\n", sum[6], sk_dot_f64(y, x, 3), sk_mix(-128, 65535, -32768, -0.5f));
    } else {
        c_add_i32(sum, b, a, 7);
        printf("direct: %d %.17g %d\n", sum[6], c_dot_f64(y, x, 3), c_mix(-128, 65535, -32768, -0.5f));
    }
    return 0;
}
