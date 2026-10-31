// Memory copies (PF-01): 40 million memcpy and memmove calls of 1 to 256 bytes at varying offsets, then 2,000 copies
// and fills of 1 MiB blocks. Prints a checksum.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(void)
{
    static unsigned char small[8192];
    for (int index = 0; index < (int)sizeof small; index++) small[index] = (unsigned char)(index * 131);
    uint32_t state = 2463534242u;
    uint64_t checksum = 0;
    for (long index = 0; index < 40000000; index++) {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        size_t length = 1 + (state & 255), from = (state >> 8) & 4095, to = 4096 + ((state >> 20) & 3071);
        if (index & 1) memmove(small + from + 1, small + from, length);
        else memcpy(small + to, small + from, length);
        checksum += small[to];
    }
    enum { block = 1 << 20 };
    unsigned char *source = malloc(block), *target = malloc(block);
    if (!source || !target) return 1;
    memset(source, 7, block);
    for (int index = 0; index < 2000; index++) {
        memcpy(target, source, block);
        memset(source, index, block / 2);
        checksum += target[index * 17 % block] + source[block - 1];
    }
    free(source);
    free(target);
    printf("%llu\n", (unsigned long long)checksum);
    return 0;
}
