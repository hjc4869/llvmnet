#include <complex.h>
#include <math.h>
#include <stdio.h>

__attribute__((noinline)) static double complex multiply(double complex left, double complex right)
{
    return left * right;
}

__attribute__((noinline)) static double complex divide(double complex left, double complex right)
{
    return left / right;
}

static void print_value(double value)
{
    if (isnan(value))
        printf("nan ");
    else
        printf("%a ", value);
}

int main(void)
{
    double cases[][4] = {
        {3, 4, 5, -6}, {0, -0.0, 2, 3}, {1e200, 1e200, 1e200, -1e200},
        {1e-200, -1e-200, 1e-200, 1e-200}, {INFINITY, 1, 2, 3},
        {1, 2, INFINITY, -INFINITY}, {NAN, 1, 2, 3}, {1, 2, 0, 0}
    };
    for (unsigned index = 0; index < sizeof cases / sizeof cases[0]; ++index) {
        double complex left;
        double complex right;
        __real__ left = cases[index][0];
        __imag__ left = cases[index][1];
        __real__ right = cases[index][2];
        __imag__ right = cases[index][3];
        double complex product = multiply(left, right);
        double complex quotient = divide(left, right);
        print_value(creal(product));
        print_value(cimag(product));
        print_value(creal(quotient));
        print_value(cimag(quotient));
        puts("");
    }
    return 0;
}