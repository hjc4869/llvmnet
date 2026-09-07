#include <stdint.h>
#include <limits.h>

static volatile uint64_t input = UINT64_C(0x123456789abcdef0);
static volatile unsigned small = 0x1234;
static volatile int maximum = INT_MAX;
static volatile double number = 6.25;

int main(void)
{
    uint64_t value = input;
    if (__builtin_bswap64(value) != UINT64_C(0xf0debc9a78563412)) return 1;
    if (__builtin_bswap32(small) != 0x34120000u) return 2;
    if (__builtin_popcountll(value) != 32) return 3;
    if (__builtin_clzll(value) != 3 || __builtin_ctzll(value) != 4) return 4;
    int result;
    if (!__builtin_add_overflow(maximum, 1, &result) || result != INT_MIN) return 5;
    if (__builtin_mul_overflow(maximum / 3, 3, &result)) return 6;
    unsigned long long unsigned_result;
    if (!__builtin_mul_overflow(value, 32ull, &unsigned_result)) return 7;
    if (__builtin_sqrt(number) != 2.5 || __builtin_fabs(-number) != number) return 8;
    uint64_t rotated = (value << 8) | (value >> 56);
    if (rotated != UINT64_C(0x3456789abcdef012)) return 9;
    return 0;
}