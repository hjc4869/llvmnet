#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <errno.h>

static pthread_mutex_t mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t condition = PTHREAD_COND_INITIALIZER;
static pthread_once_t once = PTHREAD_ONCE_INIT;
static pthread_key_t key;
static atomic_int counter;
static atomic_int destructed;
static int initialized;
static int ready;
static int go;
static void initialize(void) { initialized++; }
static void destroy(void *value) { if (value) atomic_fetch_add(&destructed, 1); }
static void *worker(void *value)
{
    pthread_once(&once, initialize);
    pthread_setspecific(key, value);
    pthread_mutex_lock(&mutex);
    ready++;
    pthread_cond_broadcast(&condition);
    while (!go) pthread_cond_wait(&condition, &mutex);
    pthread_mutex_unlock(&mutex);
    for (int index = 0; index < 5000; index++) atomic_fetch_add(&counter, 1);
    return pthread_getspecific(key);
}
int main(void)
{
    if (pthread_key_create(&key, destroy)) return 1;
    pthread_t threads[4];
    for (int index = 0; index < 4; index++)
        if (pthread_create(&threads[index], NULL, worker, (void *)(intptr_t)(index + 1))) return 2;
    pthread_mutex_lock(&mutex);
    while (ready != 4) pthread_cond_wait(&condition, &mutex);
    go = 1;
    pthread_cond_broadcast(&condition);
    pthread_mutex_unlock(&mutex);
    for (int index = 0; index < 4; index++) {
        void *result;
        if (pthread_join(threads[index], &result) || result != (void *)(intptr_t)(index + 1)) return 3;
    }
    if (counter != 20000 || initialized != 1 || destructed != 4) return 4;
    pthread_mutex_lock(&mutex);
    struct timespec past = {0, 0};
    if (pthread_cond_timedwait(&condition, &mutex, &past) != ETIMEDOUT) return 5;
    pthread_mutex_unlock(&mutex);
    if (pthread_mutex_destroy(&mutex) || pthread_cond_destroy(&condition) || pthread_key_delete(key)) return 6;
    printf("threads: count=%d initialized=%d destructed=%d\n", counter, initialized, destructed);
    return 0;
}