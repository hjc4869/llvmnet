// Signaling NaN constants and copies keep their bits. The .NET JIT keeps float constants
// as doubles, and widening a signaling NaN quiets it, so llvmnet materializes such float constants from their bits.
#include <stdio.h>
#include <string.h>

__attribute__((noinline)) static float make_float(void) { return __builtin_nansf(""); }
__attribute__((noinline)) static double make_double(void) { return __builtin_nans(""); }
__attribute__((noinline)) static long double make_long_double(void) { return __builtin_nansl(""); }
__attribute__((noinline)) static float copy_float(volatile float *source) { return *source; }

int main(void)
{
    float single = make_float();
    double wide = make_double();
    long double extended = make_long_double();
    unsigned single_bits, copied_bits, pattern = 0x7fa00001u;
    unsigned long long wide_bits, extended_bits;
    unsigned short extended_exponent;
    memcpy(&single_bits, &single, sizeof single_bits);
    memcpy(&wide_bits, &wide, sizeof wide_bits);
    memcpy(&extended_bits, &extended, sizeof extended_bits);
    memcpy(&extended_exponent, (char *)&extended + 8, sizeof extended_exponent);
    volatile float stored;
    memcpy((void *)&stored, &pattern, sizeof pattern);
    float copied = copy_float(&stored);
    memcpy(&copied_bits, &copied, sizeof copied_bits);
    printf("float %08x\ndouble %016llx\nlong double %04x %016llx\ncopy %08x\n", single_bits, wide_bits,
        extended_exponent, extended_bits, copied_bits);
    return 0;
}
