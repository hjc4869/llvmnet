// Variable-length arrays and alloca (PF-04, BE-17): a recursive search whose frames hold variable-length arrays,
// which Clang brackets with llvm.stacksave and llvm.stackrestore, and a loop that calls alloca 30 million times.
// Prints a checksum.
#include <alloca.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

static uint64_t search(int depth, int width, uint64_t seed)
{
    uint64_t board[width];
    for (int index = 0; index < width; index++) board[index] = seed * (index + 1) ^ (seed >> 7);
    if (depth == 0) return board[seed % width];
    uint64_t best = 0;
    for (int move = 0; move < 3; move++) {
        uint64_t score = search(depth - 1, width, board[move % width] + move);
        if (score > best) best = score;
    }
    return best ^ board[0];
}

static __attribute__((noinline)) uint64_t scratch(int length, uint64_t seed)
{
    unsigned char *bytes = alloca(length);
    memset(bytes, (int)seed, length);
    bytes[seed % length] ^= 0x5a;
    uint64_t sum = 0;
    for (int index = 0; index < length; index += 16) sum += bytes[index];
    return sum;
}

int main(void)
{
    uint64_t checksum = 0;
    for (int round = 0; round < 120; round++) checksum += search(9, 4 + round % 13, round + 1);
    for (long index = 0; index < 30000000; index++) checksum += scratch(16 + (index & 127), index);
    printf("%llu\n", (unsigned long long)checksum);
    return 0;
}
