/* A parallel region that captures more variables than libomp's generic C invoker passes (15): the linker gives such
 * a region a thunk that unpacks the runtime's argument array. Twenty shared arrays and a captured scalar. */
#include <omp.h>
#include <stdio.h>

int main(void)
{
    int a0[4] = {0}, a1[4] = {0}, a2[4] = {0}, a3[4] = {0}, a4[4] = {0}, a5[4] = {0}, a6[4] = {0}, a7[4] = {0}, a8[4] = {0}, a9[4] = {0};
    int b0[4] = {0}, b1[4] = {0}, b2[4] = {0}, b3[4] = {0}, b4[4] = {0}, b5[4] = {0}, b6[4] = {0}, b7[4] = {0}, b8[4] = {0}, b9[4] = {0};
    int scale = 3;
    long total = 0;
#pragma omp parallel num_threads(4) shared(a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, b0, b1, b2, b3, b4, b5, b6, b7, b8, b9) firstprivate(scale) reduction(+:total)
    {
        int me = omp_get_thread_num();
        if (me < 4) {
            a0[me] = 1 * scale; a1[me] = 2 * scale; a2[me] = 3 * scale; a3[me] = 4 * scale; a4[me] = 5 * scale;
            a5[me] = 6 * scale; a6[me] = 7 * scale; a7[me] = 8 * scale; a8[me] = 9 * scale; a9[me] = 10 * scale;
            b0[me] = 11; b1[me] = 12; b2[me] = 13; b3[me] = 14; b4[me] = 15; b5[me] = 16; b6[me] = 17; b7[me] = 18; b8[me] = 19; b9[me] = 20;
            total += me + 1;
        }
    }
    long sum = 0;
    for (int index = 0; index < 4; index++)
        sum += a0[index] + a1[index] + a2[index] + a3[index] + a4[index] + a5[index] + a6[index] + a7[index] + a8[index] + a9[index]
            + b0[index] + b1[index] + b2[index] + b3[index] + b4[index] + b5[index] + b6[index] + b7[index] + b8[index] + b9[index];
    printf("%ld %ld\n", sum, total);
    return sum != 4 * (55 * 3 + 155) || total != 10;
}
