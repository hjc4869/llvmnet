/* The x86 target intrinsics llvmnet translates, because they carry no vector data: the spin-wait hint (libstdc++'s
   atomic waits use it on x86-64) and the SSE fences. */
#include <stdio.h>

int main(void)
{
    volatile int total = 0;
    for (int i = 0; i < 4; i++) {
        __builtin_ia32_pause();
        total += i;
    }
    __builtin_ia32_mfence();
    __builtin_ia32_lfence();
    __builtin_ia32_sfence();
    printf("total=%d\n", total);
    return 0;
}
