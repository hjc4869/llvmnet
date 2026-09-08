#include <omp.h>
#include <stdio.h>

int main(void)
{
    int visits[4] = {0};
    int workers = 0;
    int total = 0;
    int shared = 0;
    omp_set_dynamic(0);
    omp_set_num_threads(4);
    if (omp_get_max_threads() != 4 || omp_in_parallel())
        return 1;
#pragma omp parallel shared(visits, workers, shared) reduction(+:total)
    {
        int identity = omp_get_thread_num();
#pragma omp single
        workers = omp_get_num_threads();
        if (identity < 4)
            visits[identity]++;
#pragma omp for schedule(dynamic, 3)
        for (int index = 0; index < 1000; ++index)
            total += index;
#pragma omp critical
        shared++;
#pragma omp barrier
        if (shared != 4)
            visits[identity] = -1;
    }
    if (workers != 4 || total != 499500 || shared != 4)
        return 2;
    for (int index = 0; index < 4; ++index)
        if (visits[index] != 1)
            return 3;
    puts("OpenMP: four workers, dynamic scheduling, reduction and barriers passed");
    return 0;
}