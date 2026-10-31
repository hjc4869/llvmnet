/* OpenMP worker stacks: libomp sizes them by RLIMIT_STACK unless OMP_STACKSIZE says otherwise; a worker uses the
 * number of megabytes given on the command line. */
#include <alloca.h>
#include <omp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(int argc, char **argv)
{
    size_t megabytes = argc > 1 ? strtoul(argv[1], 0, 10) : 6;
    int ok = 0;
#pragma omp parallel num_threads(2) reduction(+:ok)
    {
        if (omp_get_thread_num() == 1) {
            char *block = alloca(megabytes << 20);
            memset(block, 1, megabytes << 20);
            ok += block[(megabytes << 20) - 1];
        }
    }
    printf("%zu MB on an OpenMP worker: %d\n", megabytes, ok);
    return 0;
}
