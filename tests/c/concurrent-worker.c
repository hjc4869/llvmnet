#include <omp.h>

int concurrent_worker(int index)
{
    return 8 * index + omp_get_thread_num();
}