// Atomic operations (PF-01, PF-02): 40 million sequentially consistent fetch-adds, compare-exchanges, exchanges, loads
// and stores on 32- and 64-bit locations from one thread, then four threads adding to one shared counter 5 million
// times each. Prints the final values.
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>

static _Atomic long shared;

static void *add(void *argument)
{
    for (long index = 0; index < 5000000; index++)
        atomic_fetch_add_explicit(&shared, (long)argument, memory_order_relaxed);
    return 0;
}

int main(void)
{
    static _Atomic long wide;
    static _Atomic int narrow;
    long checksum = 0;
    for (long index = 0; index < 8000000; index++) {
        atomic_fetch_add(&wide, index & 7);
        int expected = atomic_load(&narrow);
        while (!atomic_compare_exchange_weak(&narrow, &expected, expected ^ (int)index)) {}
        checksum += atomic_exchange(&wide, atomic_load(&wide) + 1) & 15;
        atomic_store(&narrow, atomic_load(&narrow) + 3);
    }
    pthread_t threads[4];
    for (long thread = 0; thread < 4; thread++)
        if (pthread_create(&threads[thread], 0, add, (void *)(thread + 1))) return 1;
    for (int thread = 0; thread < 4; thread++) pthread_join(threads[thread], 0);
    printf("%ld %d %ld %ld\n", atomic_load(&wide), atomic_load(&narrow), checksum, atomic_load(&shared));
    return 0;
}
