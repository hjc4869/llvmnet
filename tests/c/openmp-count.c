/* The default OpenMP team size in portable mode: Environment.ProcessorCount, which honors cgroup limits, affinity
 * and DOTNET_PROCESSOR_COUNT, unless OMP_NUM_THREADS says otherwise. Prints the team size a parallel region gets. */
#include <omp.h>
#include <stdio.h>

int main(void)
{
    int team = 0;
#pragma omp parallel
    {
#pragma omp single
        team = omp_get_num_threads();
    }
    printf("%d %d\n", omp_get_max_threads(), team);
    return 0;
}
