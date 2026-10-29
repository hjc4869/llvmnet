/* musl threads over the host clone/futex layer (LC-02): creation, join values, mutexes, condition
 * variables, once, keys with destructors, __thread, detached threads, semaphores, rwlocks and barriers. */
#include <pthread.h>
#include <semaphore.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#define THREADS 8
#define ITERATIONS 20000
static pthread_mutex_t mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t condition = PTHREAD_COND_INITIALIZER;
static pthread_once_t once = PTHREAD_ONCE_INIT;
static pthread_key_t key;
static pthread_rwlock_t rwlock = PTHREAD_RWLOCK_INITIALIZER;
static pthread_barrier_t barrier;
static sem_t semaphore;
static long counter, ready, destroyed, once_runs, rw_sum;
static _Atomic long atomic_counter;
static __thread long thread_local_value;
static void run_once(void) { once_runs++; }
static void destroy(void *value) { __atomic_fetch_add(&destroyed, (long)(intptr_t)value, __ATOMIC_SEQ_CST); }
static void *worker(void *argument)
{
    long id = (long)(intptr_t)argument;
    pthread_once(&once, run_once);
    pthread_setspecific(key, (void *)(intptr_t)(id + 1));
    thread_local_value = id * 100;
    for (int i = 0; i < ITERATIONS; i++) {
        pthread_mutex_lock(&mutex); counter++; pthread_mutex_unlock(&mutex);
        atomic_counter++;
        if (i % 1000 == 0) { pthread_rwlock_rdlock(&rwlock); long seen = rw_sum; pthread_rwlock_unlock(&rwlock); (void)seen; }
    }
    pthread_rwlock_wrlock(&rwlock); rw_sum += id; pthread_rwlock_unlock(&rwlock);
    pthread_barrier_wait(&barrier);
    pthread_mutex_lock(&mutex); ready++; pthread_cond_broadcast(&condition); pthread_mutex_unlock(&mutex);
    sem_post(&semaphore);
    if (thread_local_value != id * 100) return (void *)-1;
    if (id == THREADS - 1) pthread_exit((void *)(intptr_t)(id * 3));
    return (void *)(intptr_t)(id * 3);
}
static void *detached(void *argument) { sem_post(&semaphore); return argument; }
int main(void)
{
    pthread_key_create(&key, destroy);
    pthread_barrier_init(&barrier, 0, THREADS);
    sem_init(&semaphore, 0, 0);
    pthread_t threads[THREADS];
    for (long id = 0; id < THREADS; id++) if (pthread_create(&threads[id], 0, worker, (void *)(intptr_t)id)) { puts("create failed"); return 1; }
    pthread_mutex_lock(&mutex);
    while (ready < THREADS) pthread_cond_wait(&condition, &mutex);
    pthread_mutex_unlock(&mutex);
    long results = 0;
    for (int id = 0; id < THREADS; id++) { void *value; pthread_join(threads[id], &value); results += (long)(intptr_t)value; }
    for (int id = 0; id < THREADS; id++) sem_wait(&semaphore);
    pthread_attr_t attributes; pthread_attr_init(&attributes); pthread_attr_setdetachstate(&attributes, PTHREAD_CREATE_DETACHED);
    pthread_t loose; pthread_create(&loose, &attributes, detached, 0); sem_wait(&semaphore);
    printf("counter %ld atomic %ld ready %ld once %ld joined %ld rw %ld destroyed %ld main_tls %ld\n",
        counter, (long)atomic_counter, ready, once_runs, results, rw_sum, destroyed, thread_local_value);
    return 0;
}
