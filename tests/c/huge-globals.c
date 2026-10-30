#include <stddef.h>
#include <stdio.h>

/* Globals beyond 2 GB: a zero-filled array and a record whose initializer has
   data past the 2 GB mark. Built at -O0 so the reads are not folded; only touched pages are committed. */
static unsigned char zeros[(size_t)3 << 30];
static struct { unsigned char pad[((size_t)1 << 31) + 12345]; int tail[4]; } tailed = { .tail = { 1, 2, 3, 4 } };

int main(void)
{
    zeros[sizeof(zeros) - 1] = 7;
    if (zeros[0] != 0 || zeros[(size_t)2 << 30] != 0 || zeros[sizeof(zeros) - 1] != 7)
        return 1;
    if (tailed.pad[0] != 0 || tailed.pad[sizeof(tailed.pad) - 1] != 0 || tailed.tail[0] + tailed.tail[3] != 5)
        return 2;
    printf("globals beyond 2 GB: %zu and %zu bytes passed\n", sizeof(zeros), sizeof(tailed));
    return 0;
}
