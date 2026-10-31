/* The long double math intrinsics (BE-37): with -fno-math-errno Clang turns sqrtl, logl and the others into
 * llvm.<op>.f80 intrinsics, which the linker maps to the C library's long double functions. Results are printed
 * with 15 significant digits, so that the last-bit differences between musl's generic C and x87 code do not show. */
#include <math.h>
#include <stdio.h>

int main(void)
{
    static const long double values[] = { 0.5L, 1.0L, 2.75L, -3.5L, 10.0L, 1.0e-5L, 123456.789L, 0.1L, 7.0L / 3 };
    for (unsigned index = 0; index < sizeof values / sizeof values[0]; index++) {
        long double x = values[index], magnitude = fabsl(x);
        int exponent;
        long double fraction = frexpl(x, &exponent);
        printf("%.15Lg: sqrt %.15Lg ceil %.15Lg trunc %.15Lg rint %.15Lg nearbyint %.15Lg round %.15Lg roundeven %.15Lg\n",
            x, sqrtl(magnitude), ceill(x), truncl(x), rintl(x), nearbyintl(x), roundl(x), __builtin_roundevenl(x));
        printf("  sin %.15Lg cos %.15Lg tan %.15Lg exp %.15Lg exp2 %.15Lg exp10 %.15Lg\n",
            sinl(x), cosl(x), tanl(x), expl(x), exp2l(x), __builtin_exp10l(x));
        printf("  log %.15Lg log2 %.15Lg log10 %.15Lg pow %.15Lg powi %.15Lg ldexp %.15Lg frexp %.15Lg %d\n",
            logl(magnitude), log2l(magnitude), log10l(magnitude), powl(magnitude, 1.5L), __builtin_powil(x, 3), ldexpl(x, 4), fraction, exponent);
    }
    return 0;
}
