/* musl libm on the portable ABI: double and float results must equal native musl bit for bit, since the same C
 * implementations are compiled. long double functions are printed rounded, because native x86-64 musl
 * implements several of them with x87 assembly while the port uses the generic C versions. */
#include <math.h>
#include <stdio.h>
#include <fenv.h>
int main(void)
{
    double inputs[] = { 0.0, -0.0, 0.5, 1.0, -1.25, 3.0, 10.0, 1e-300, 123.456, -987.25, 1e10 };
    for (unsigned i = 0; i < sizeof inputs / sizeof *inputs; i++) {
        double x = inputs[i];
        printf("%a: %a %a %a %a %a %a %a %a\n", x, sin(x), cos(x), tan(x), exp(x), log(fabs(x) + 1), atan(x), cbrt(x), sqrt(fabs(x)));
        printf("    %a %a %a %a %a %a %a %a\n", sinh(x / 100), tanh(x), erf(x / 10), lgamma(x + 0.5), tgamma(x / 10 + 1), expm1(x / 100), log1p(fabs(x)), hypot(x, 3));
        printf("    %a %a %a %a %a %a\n", pow(fabs(x) + 0.5, 1.7), atan2(x, -2.0), fmod(x, 0.7), remainder(x, 0.7), fma(x, 1.1, -0.3), nextafter(x, 1.0));
        printf("    %a %a %a %a %ld %d\n", (double)sinf((float)x), (double)expf((float)x / 10), (double)logf((float)fabs(x) + 1), (double)powf(2.0f, (float)x / 8), lround(x), ilogb(x + 1));
        long double y = (long double)x;
        printf("    %.15Lg %.15Lg %.15Lg %.15Lg %.15Lg\n", sqrtl(fabsl(y)), expl(y / 100), logl(fabsl(y) + 1), sinl(y), powl(fabsl(y) + 1, 0.5L));
    }
    printf("rounding %d %a %a %a %a\n", fegetround() == FE_TONEAREST, rint(2.5), nearbyint(-3.5), round(-2.5), trunc(-7.9));
    printf("classify %d %d %d %d %d\n", fpclassify(0.0), fpclassify(1e-310), isnan(NAN), isinf(-INFINITY), signbit(-0.0) != 0);
    int exponent; double fraction = frexp(1536.0, &exponent);
    printf("frexp %a %d ldexp %a modf %a\n", fraction, exponent, ldexp(0.75, 11), modf(-3.25, &fraction));
    return 0;
}
