/* Calls tests/kernels/nasm/add.asm, assembled by nasm, and changes a variable it defines. */
#include <stdint.h>
#include <stdio.h>

int64_t nk_add3(int64_t a, int64_t b, int64_t c);
extern int64_t nk_bias;

int main(void)
{
    nk_bias += 5;
    printf("%lld %lld\n", (long long)nk_add3(1, 2, 3), (long long)nk_bias);
    return 0;
}
