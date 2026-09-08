#include <math.h>
#include <errno.h>
static volatile double value = 2.5;
static volatile float single = -2.5f;
static volatile double negative = -1;
int main(void)
{
    if (round(value) != 3 || roundf(single) != -3) return 1;
    if (llrint(value) != 2 || llrintf(single) != -2) return 2;
    if (fabs(cbrt(value * 10 + 2) - 3) > 1e-14) return 3;
    if (fabs(hypot(value * 1.2, value * 1.6) - 5) > 1e-14) return 4;
    if (fabs(erf(value / 2.5) - 0.8427007929497149) > 1e-14) return 5;
    if (fabsf(atanf(single) + 1.19028994968f) > 1e-6f) return 6;
    errno = 0;
    if (!isnan(sqrt(negative)) || errno != EDOM) return 7;
    double integral;
    float integral_single;
    if (modf(value, &integral) != 0.5 || integral != 2) return 8;
    if (modff(single, &integral_single) != -0.5f || integral_single != -2) return 9;
    double fraction = modf(-INFINITY, &integral);
    if (fraction != 0 || !signbit(fraction) || !isinf(integral) || !signbit(integral)) return 10;
    if (fabs(atanh(value / 5) - 0.5493061443340548) > 1e-14) return 11;
    if (fabsf(atanhf(single / 5) + 0.5493061443340548f) > 1e-6f) return 12;
    if (!signbit(atanh(-0.0)) || !signbit(atanhf(-0.0f))) return 13;
    errno = 0;
    if (!isnan(atanh(value)) || errno != EDOM) return 14;
    errno = 0;
    if (atanh(negative) != -INFINITY || errno != ERANGE) return 15;
    errno = 0;
    if (!isnan(atanhf(single)) || errno != EDOM) return 16;
    errno = 0;
    if (atanhf(1.0f) != INFINITY || errno != ERANGE) return 17;
    errno = 0;
    if (!isnan(atanh(NAN)) || !isnan(atanhf(NAN)) || errno != 0) return 18;
    return 0;
}