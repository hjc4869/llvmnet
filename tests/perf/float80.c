// long double arithmetic (PF-01): x87 80-bit values, which portable mode computes in software (Float80). 1 million
// steps of a recurrence with addition, multiplication, division and comparison, then 50,000 square roots.
// Prints the results with 18 significant digits.
#include <math.h>
#include <stdio.h>

int main(void)
{
    long double sum = 0, product = 1, x = 0.5L;
    for (long index = 1; index <= 1000000; index++) {
        x = x * 3.7L * (1.0L - x);
        sum += x / (long double)index;
        product *= 1.0L + x / 1048576.0L;
        if (product > 1e300L) product /= 1e300L;
    }
    long double roots = 0;
    for (long index = 1; index <= 50000; index++) roots += sqrtl((long double)index);
    printf("%.18Lg %.18Lg %.18Lg %.18Lg\n", sum, product, x, roots);
    return 0;
}
