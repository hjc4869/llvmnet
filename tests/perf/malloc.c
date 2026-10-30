// malloc/free throughput (scripts/bench-runtime.sh): 20 million pairs of 16 to 4,096 bytes over 4,096 live slots.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

int main(void)
{
    static unsigned char *slots[4096];
    uint64_t state = 88172645463325252u, sum = 0;
    for (long iteration = 0; iteration < 20000000; iteration++) {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        size_t slot = state % 4096, size = 16 + (state >> 20) % 4081;
        if (slots[slot]) sum += slots[slot][0];
        free(slots[slot]);
        slots[slot] = malloc(size);
        if (!slots[slot]) return 1;
        slots[slot][0] = (unsigned char)size;
    }
    for (size_t slot = 0; slot < 4096; slot++) free(slots[slot]);
    printf("%llu\n", (unsigned long long)sum);
    return 0;
}
