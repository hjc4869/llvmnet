#include <setjmp.h>
#include <stdio.h>

__attribute__((noinline)) static int exercise(int size)
{
    jmp_buf saved;
    volatile int iterations = 0;
    volatile int total = 0;
    int result = setjmp(saved);
    if (result != 0 && result != 17)
        return 1;
    if (iterations == 10000)
        return total != 10000 * 59;
    volatile unsigned char buffer[size];
    buffer[0] = 23;
    buffer[size - 1] = 36;
    total += buffer[0] + buffer[size - 1];
    ++iterations;
    longjmp(saved, 17);
}

int main(void)
{
    if (exercise(65536))
        return 1;
    puts("dynamic allocations restored by longjmp");
    return 0;
}