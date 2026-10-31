// Mutex and condition-variable contention (PF-02): four threads take one pthread mutex 2 million times each to update
// shared state, then two threads pass a token back and forth 200,000 times through a condition variable. Prints the
// final values.
#include <pthread.h>
#include <stdio.h>

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t changed = PTHREAD_COND_INITIALIZER;
static long total, turn;

static void *contend(void *argument)
{
    for (long index = 0; index < 2000000; index++) {
        pthread_mutex_lock(&lock);
        total += (long)argument ^ (index & 3);
        pthread_mutex_unlock(&lock);
    }
    return 0;
}

static void *ping(void *argument)
{
    long self = (long)argument;
    for (long index = 0; index < 200000; index++) {
        pthread_mutex_lock(&lock);
        while ((turn & 1) != self) pthread_cond_wait(&changed, &lock);
        turn++;
        pthread_cond_signal(&changed);
        pthread_mutex_unlock(&lock);
    }
    return 0;
}

int main(void)
{
    pthread_t threads[4];
    for (long thread = 0; thread < 4; thread++)
        if (pthread_create(&threads[thread], 0, contend, (void *)thread)) return 1;
    for (int thread = 0; thread < 4; thread++) pthread_join(threads[thread], 0);
    for (long thread = 0; thread < 2; thread++)
        if (pthread_create(&threads[thread], 0, ping, (void *)thread)) return 1;
    for (int thread = 0; thread < 2; thread++) pthread_join(threads[thread], 0);
    printf("%ld %ld\n", total, turn);
    return 0;
}
