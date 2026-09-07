#include <stdint.h>
#include <stdio.h>
#include <stdarg.h>
static volatile uint64_t high = UINT64_C(0x0123456789abcdef);
static volatile uint64_t low = UINT64_C(0xfedcba9876543210);
static volatile double negative = -12345.75;
static volatile long double extended = 0x1p100L;
__attribute__((noinline)) static __uint128_t variadic_sum(int count, ...)
{
    va_list arguments;
    va_start(arguments, count);
    __uint128_t result = 0;
    for (int index = 0; index < count; index++) result += va_arg(arguments, __uint128_t);
    va_end(arguments);
    return result;
}
int main(void)
{
    __uint128_t value = ((__uint128_t)high << 64) | low;
    __uint128_t product = value * 17;
    if (product / 17 != value || product % 17 != 0) return 1;
    if ((value << 67) >> 67 != (value & (((__uint128_t)1 << 61) - 1))) return 2;
    __int128 signed_value = -(__int128)value;
    if (signed_value >= 0 || (__uint128_t)-signed_value != value || (signed_value >> 120) != -2) return 3;
    if (signed_value / 17 * 17 + signed_value % 17 != signed_value) return 4;
    __uint128_t maximum = ~(__uint128_t)0;
    if (maximum + 1 != 0 || maximum < value) return 5;
    __uint128_t overflowed;
    if (!__builtin_add_overflow(maximum, (__uint128_t)1, &overflowed) || overflowed != 0) return 8;
    if (__builtin_mul_overflow(value, (__uint128_t)17, &overflowed) || overflowed != product) return 9;
    if ((__int128)negative != -12345 || (double)(__int128)negative != -12345) return 6;
    __int128 power = (__int128)extended;
    if (power != ((__int128)1 << 100) || (long double)power != extended) return 7;
    if (variadic_sum(3, value, value * 2, value * 3) != value * 6) return 10;
    printf("wide: %016llx%016llx\n", (unsigned long long)(product >> 64), (unsigned long long)product);
    return 0;
}