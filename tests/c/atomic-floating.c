#include <math.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>

static _Atomic float single;
static _Atomic double extended;

static void *update(void *argument)
{
    for (int index = 0; index < 10000; ++index) {
        atomic_fetch_add_explicit(&single, 0.25f, memory_order_relaxed);
        atomic_fetch_add_explicit(&extended, 0.5, memory_order_relaxed);
    }
    return argument;
}

int main(void)
{
    atomic_store(&single, 1.5f);
    atomic_store(&extended, 0x1.0000000000001p0);
    if (atomic_fetch_add(&single, 0.25f) != 1.5f || atomic_load(&single) != 1.75f)
        return 1;
    if (atomic_fetch_sub(&single, 0.5f) != 1.75f || atomic_load(&single) != 1.25f)
        return 2;
    if (atomic_fetch_add(&extended, 0x1p-52) != 0x1.0000000000001p0 || atomic_load(&extended) != 0x1.0000000000002p0)
        return 3;
    if (atomic_fetch_sub(&extended, 0x1p-52) != 0x1.0000000000002p0 || atomic_load(&extended) != 0x1.0000000000001p0)
        return 4;
    atomic_store(&single, -0.0f);
    if (!signbit(atomic_fetch_add(&single, -0.0f)) || !signbit(atomic_load(&single)))
        return 5;
    atomic_store(&extended, INFINITY);
    if (!isinf(atomic_fetch_sub(&extended, INFINITY)) || !isnan(atomic_load(&extended)))
        return 6;
    atomic_store(&single, 0.0f);
    atomic_store(&extended, 0.0);
    pthread_t workers[4];
    for (int index = 0; index < 4; ++index)
        if (pthread_create(&workers[index], NULL, update, NULL))
            return 7;
    for (int index = 0; index < 4; ++index)
        if (pthread_join(workers[index], NULL))
            return 8;
    if (atomic_load(&single) != 10000.0f || atomic_load(&extended) != 20000.0)
        return 9;
    puts("floating atomics: old values, IEEE results and four-thread updates passed");
    return 0;
}