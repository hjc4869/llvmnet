/* Thread lifecycle stress (plan item LC-22): 10,000 threads created and joined in batches of 64, detached threads
 * that are still running when main returns, pthread_once under contention, thread-specific data destructors,
 * and timed condition waits. Prints totals that must equal the native build's. */
#include <errno.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

static long sum;
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_once_t once = PTHREAD_ONCE_INIT;
static int once_runs, destructor_runs;
static pthread_key_t key;

static void initialize(void) { once_runs++; }
static void destroy(void *value) { __atomic_fetch_add(&destructor_runs, 1, __ATOMIC_SEQ_CST); free(value); }

static void *worker(void *argument)
{
    pthread_once(&once, initialize);
    int *value = malloc(sizeof *value);
    *value = (int)(long)argument;
    pthread_setspecific(key, value);
    pthread_mutex_lock(&lock);
    sum += (long)argument;
    pthread_mutex_unlock(&lock);
    return argument;
}

static void *sleeper(void *argument)
{
    struct timespec pause = {0, 50000000};
    nanosleep(&pause, 0);
    return argument;
}

int main(void)
{
    pthread_key_create(&key, destroy);
    long joined = 0;
    pthread_t threads[64];
    for (int round = 0; round < 10000 / 64 + 1; round++) {
        int count = round == 10000 / 64 ? 10000 % 64 : 64;
        for (int index = 0; index < count; index++)
            if (pthread_create(&threads[index], 0, worker, (void *)(long)(round * 64 + index))) return 1;
        for (int index = 0; index < count; index++) {
            void *result;
            if (pthread_join(threads[index], &result)) return 2;
            joined += (long)result;
        }
    }
    pthread_cond_t condition = PTHREAD_COND_INITIALIZER;
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_nsec += 20000000;
    if (deadline.tv_nsec >= 1000000000) { deadline.tv_sec++; deadline.tv_nsec -= 1000000000; }
    pthread_mutex_lock(&lock);
    int timed = pthread_cond_timedwait(&condition, &lock, &deadline);
    pthread_mutex_unlock(&lock);
    printf("threads joined: 10000, sum %ld %ld, once %d, destructors %d, timed wait %s\n", sum, joined, once_runs,
           destructor_runs, timed == ETIMEDOUT ? "ETIMEDOUT" : "other");
    fflush(stdout);
    for (int index = 0; index < 8; index++) {
        pthread_t detached;
        pthread_create(&detached, 0, sleeper, 0);
        pthread_detach(detached);
    }
    return 0;
}
