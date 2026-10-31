// Indirect calls (PF-01, PF-05): 200 million calls through a table of eight functions, selected by a pseudo-random
// index so the target changes from call to call, then qsort with a comparison function over 2 million integers.
// Prints a checksum.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

static uint32_t add(uint32_t a, uint32_t b) { return a + b; }
static uint32_t sub(uint32_t a, uint32_t b) { return a - b; }
static uint32_t mul(uint32_t a, uint32_t b) { return a * b + 1; }
static uint32_t xor_(uint32_t a, uint32_t b) { return a ^ b; }
static uint32_t rotate(uint32_t a, uint32_t b) { return a << (b & 31) | a >> (-b & 31); }
static uint32_t mix(uint32_t a, uint32_t b) { return (a ^ b) * 0x9e3779b9u; }
static uint32_t low(uint32_t a, uint32_t b) { return a < b ? a : b; }
static uint32_t high(uint32_t a, uint32_t b) { return a > b ? a : b; }

static int compare(const void *left, const void *right)
{
    uint32_t a = *(const uint32_t *)left, b = *(const uint32_t *)right;
    return (a > b) - (a < b);
}

uint32_t (*volatile table[8])(uint32_t, uint32_t) = {add, sub, mul, xor_, rotate, mix, low, high};

int main(void)
{
    uint32_t state = 2463534242u, value = 1;
    for (long index = 0; index < 200000000; index++) {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        value = table[state & 7](value, state);
    }
    enum { count = 2000000 };
    uint32_t *numbers = malloc(count * sizeof *numbers);
    if (!numbers) return 1;
    for (int index = 0; index < count; index++) {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        numbers[index] = state;
    }
    qsort(numbers, count, sizeof *numbers, compare);
    uint64_t checksum = value;
    for (int index = 0; index < count; index += 1000) checksum = checksum * 31 + numbers[index];
    free(numbers);
    printf("%llu\n", (unsigned long long)checksum);
    return 0;
}
