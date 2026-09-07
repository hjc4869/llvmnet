#include <stdint.h>
#include <math.h>
#include <float.h>

static volatile long double large = 18446744073709551616.0L;
static volatile long double one = 1.0L;
static volatile long double two = 2.0L;
static volatile long double huge = 0x1p10000L;
static volatile long double tiny = 0x1p-16400L;
static volatile double value = -2.75;

int main(void)
{
    if ((large + two) - large != two) return 1;
    if ((large + one) - large != 0.0L) return 2;
    if ((huge * two) / huge != two) return 3;
    if ((tiny * huge) / huge != tiny) return 4;
    if ((long double)value != -2.75L || (double)((long double)value / two) != -1.375) return 5;
    if ((long long)(large / two - one) != INT64_MAX) return 6;
    if ((unsigned long long)(large - one) != UINT64_MAX) return 7;
    long double nan = (one - one) / (one - one);
    if (nan == nan || nan < one || nan >= one) return 8;
    long double infinity = one / (one - one);
    if (!(infinity > huge) || (double)infinity != INFINITY) return 9;
    if ((float)(one + 0x1p-24L + 0x1p-60L) != 0x1.000002p0f) return 10;
    long double cancelled = (1.0L + 0x1p-32L) * (1.0L - 0x1p-32L) - 1.0L;
    if (cancelled != -0x1p-64L) return 11;
    return 0;
}