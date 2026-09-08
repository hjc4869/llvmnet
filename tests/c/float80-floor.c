#include <float.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

static volatile long double values[] = {
    0.0L, -0.0L, 0.75L, -0.75L, 1.0L, -1.0L,
    1.0L + 0x1p-63L, -1.0L - 0x1p-63L,
    9007199254740993.5L, -9007199254740993.5L,
    9223372036854775807.5L, -9223372036854775807.5L,
    0x1p16380L, -0x1p16380L, LDBL_TRUE_MIN, -LDBL_TRUE_MIN,
    __builtin_infl(), -__builtin_infl(), __builtin_nanl("")
};

static const long double expected[] = {
    0.0L, -0.0L, 0.0L, -1.0L, 1.0L, -1.0L,
    1.0L, -2.0L, 9007199254740993.0L, -9007199254740994.0L,
    9223372036854775807.0L, -9223372036854775808.0L,
    0x1p16380L, -0x1p16380L, 0.0L, -1.0L,
    __builtin_infl(), -__builtin_infl(), __builtin_nanl("")
};

int main(void)
{
    for (unsigned index = 0; index < sizeof(values) / sizeof(values[0]); ++index) {
        long double result = __builtin_floorl(values[index]);
        if (expected[index] != expected[index]) {
            if (result == result)
                return 1;
        } else if (result != expected[index]) {
            return 2;
        }
        if (result == 0.0L) {
            uint16_t result_sign;
            uint16_t expected_sign;
            memcpy(&result_sign, (unsigned char *)&result + 8, 2);
            memcpy(&expected_sign, (const unsigned char *)&expected[index] + 8, 2);
            if (result_sign != expected_sign)
                return 3;
        }
    }
    puts("Float80 floor preserves extended precision and signed zeros");
    return 0;
}