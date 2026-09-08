#if PROBE == 1
#include <stdlib.h>
int main(void) { return system(0); }
#elif PROBE == 2
#include <unistd.h>
int main(void) { return dup2(1, 2); }
#elif PROBE == 3
#include <stdio.h>
int main(void) { return freopen("probe.txt", "w", stdout) == 0; }
#elif PROBE == 4
#include <math.h>
long double probe(long double value) { return hypotl(value, value); }
int main(void) { return probe(1) < 1; }
#elif PROBE == 5
extern double _Complex cexp(double _Complex);
int main(void) { return __real__ cexp(1.0) < 2.0; }
#elif PROBE == 6
int main(void)
{
    int count = 0;
#pragma omp parallel reduction(+:count)
    count += 1;
    return count < 1;
}
#elif PROBE == 7
#include <sys/types.h>
#if !defined(SPEC) || defined(SPEC_PTHREAD_STUBS_NEED_PTHREAD_TYPES)
typedef struct { void *storage; } pthread_mutex_t;
#endif
pthread_mutex_t mutex;
int main(void) { return 0; }
#elif PROBE == 9
extern int open(const char *, int, int, long);
int main(void) { return open("probe.txt", 0, 0, 9); }
#else
int main(void) { return 0; }
#endif