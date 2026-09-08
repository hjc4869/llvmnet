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
    return 0;
}