// libm on deterministic random inputs: prints the bits of every result so tests/libm-accuracy.sh can compare
// the portable build with native musl (same C code, so equal bits) and with the host libm (maximum ULP error).
// Inputs cycle through four ranges: any bit pattern, exponents within 2^-30..2^30, [-4, 4) and [-1, 1).
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

enum { COUNT = 2000 };
static uint64_t state = 0x9e3779b97f4a7c15u;

static uint64_t next(void)
{
    state ^= state >> 12;
    state ^= state << 25;
    state ^= state >> 27;
    return state * 2685821657736338717u;
}

static double random_double(int range)
{
    uint64_t bits = next();
    double value;
    switch (range) {
    case 0:
        memcpy(&value, &bits, sizeof value);
        return value;
    case 1:
        bits = (bits & 0x800fffffffffffffu) | (uint64_t)(1023 - 30 + (int)(next() % 61)) << 52;
        memcpy(&value, &bits, sizeof value);
        return value;
    case 2:
        return ((double)(bits >> 11) / 9007199254740992.0 * 2 - 1) * 4;
    default:
        return (double)(bits >> 11) / 9007199254740992.0 * 2 - 1;
    }
}

static float random_float(int range)
{
    if (range != 0) return (float)random_double(range);
    uint32_t bits = (uint32_t)(next() >> 32);
    float value;
    memcpy(&value, &bits, sizeof value);
    return value;
}

static long double random_long_double(int range)
{
    long double value = random_double(range);
    uint64_t mantissa;
    uint16_t exponent;
    memcpy(&mantissa, &value, sizeof mantissa);
    memcpy(&exponent, (char *)&value + 8, sizeof exponent);
    // Fill the 11 mantissa bits a double cannot carry, keeping the encoding canonical.
    if ((exponent & 0x7fff) != 0 && (exponent & 0x7fff) != 0x7fff) {
        mantissa |= next() >> 53;
        memcpy(&value, &mantissa, sizeof mantissa);
    }
    return value;
}

static void print_float(float value)
{
    uint32_t bits;
    memcpy(&bits, &value, sizeof bits);
    printf("%08x\n", bits);
}

static void print_double(double value)
{
    uint64_t bits;
    memcpy(&bits, &value, sizeof bits);
    printf("%016llx\n", (unsigned long long)bits);
}

static void print_long_double(long double value)
{
    uint64_t mantissa;
    uint16_t exponent;
    memcpy(&mantissa, &value, sizeof mantissa);
    memcpy(&exponent, (char *)&value + 8, sizeof exponent);
    printf("%04x%016llx\n", exponent, (unsigned long long)mantissa);
}

#define UNARY(name, kind) { #name, kind, (void (*)(void))name, 1 }
#define BINARY(name, kind) { #name, kind, (void (*)(void))name, 2 }

static const struct function {
    const char *name;
    char kind;
    void (*address)(void);
    int arity;
} functions[] = {
    UNARY(acos, 'd'), UNARY(asin, 'd'), UNARY(atan, 'd'), BINARY(atan2, 'd'),
    UNARY(cos, 'd'), UNARY(sin, 'd'), UNARY(tan, 'd'), UNARY(cosh, 'd'),
    UNARY(sinh, 'd'), UNARY(tanh, 'd'), UNARY(acosh, 'd'), UNARY(asinh, 'd'),
    UNARY(atanh, 'd'), UNARY(exp, 'd'), UNARY(exp2, 'd'), UNARY(expm1, 'd'),
    UNARY(log, 'd'), UNARY(log10, 'd'), UNARY(log1p, 'd'), UNARY(log2, 'd'),
    UNARY(cbrt, 'd'), UNARY(sqrt, 'd'), BINARY(hypot, 'd'), BINARY(pow, 'd'),
    UNARY(erf, 'd'), UNARY(erfc, 'd'), UNARY(lgamma, 'd'), UNARY(tgamma, 'd'),
    BINARY(fmod, 'd'), BINARY(remainder, 'd'), UNARY(j0, 'd'), UNARY(j1, 'd'),
    UNARY(y0, 'd'), UNARY(y1, 'd'),
    UNARY(acosf, 'f'), UNARY(asinf, 'f'), UNARY(atanf, 'f'), BINARY(atan2f, 'f'),
    UNARY(cosf, 'f'), UNARY(sinf, 'f'), UNARY(tanf, 'f'), UNARY(coshf, 'f'),
    UNARY(sinhf, 'f'), UNARY(tanhf, 'f'), UNARY(acoshf, 'f'), UNARY(asinhf, 'f'),
    UNARY(atanhf, 'f'), UNARY(expf, 'f'), UNARY(exp2f, 'f'), UNARY(expm1f, 'f'),
    UNARY(logf, 'f'), UNARY(log10f, 'f'), UNARY(log1pf, 'f'), UNARY(log2f, 'f'),
    UNARY(cbrtf, 'f'), UNARY(sqrtf, 'f'), BINARY(hypotf, 'f'), BINARY(powf, 'f'),
    UNARY(erff, 'f'), UNARY(erfcf, 'f'), UNARY(lgammaf, 'f'), UNARY(tgammaf, 'f'),
    BINARY(fmodf, 'f'),
    UNARY(acosl, 'l'), UNARY(asinl, 'l'), UNARY(atanl, 'l'),
    BINARY(atan2l, 'l'), UNARY(cosl, 'l'), UNARY(sinl, 'l'),
    UNARY(tanl, 'l'), UNARY(coshl, 'l'), UNARY(sinhl, 'l'),
    UNARY(tanhl, 'l'), UNARY(expl, 'l'), UNARY(exp2l, 'l'),
    UNARY(expm1l, 'l'), UNARY(logl, 'l'), UNARY(log10l, 'l'),
    UNARY(log1pl, 'l'), UNARY(log2l, 'l'), UNARY(cbrtl, 'l'),
    UNARY(sqrtl, 'l'), BINARY(hypotl, 'l'), BINARY(powl, 'l'),
    UNARY(erfl, 'l'), UNARY(erfcl, 'l'), UNARY(lgammal, 'l'),
    UNARY(tgammal, 'l'), BINARY(fmodl, 'l'),
};

int main(void)
{
    for (size_t index = 0; index < sizeof functions / sizeof functions[0]; index++) {
        const struct function *function = &functions[index];
        printf("# %s %c %d\n", function->name, function->kind, COUNT);
        for (int input = 0; input < COUNT; input++) {
            int range = input % 4;
            switch (function->kind) {
            case 'd': {
                double x = random_double(range), y = random_double(range);
                print_double(function->arity == 1 ? ((double (*)(double))function->address)(x)
                                                  : ((double (*)(double, double))function->address)(x, y));
                break;
            }
            case 'f': {
                float x = random_float(range), y = random_float(range);
                print_float(function->arity == 1 ? ((float (*)(float))function->address)(x)
                                                 : ((float (*)(float, float))function->address)(x, y));
                break;
            }
            default: {
                long double x = random_long_double(range), y = random_long_double(range);
                print_long_double(function->arity == 1 ? ((long double (*)(long double))function->address)(x)
                                                       : ((long double (*)(long double, long double))function->address)(x, y));
            }
            }
        }
    }
    return 0;
}
