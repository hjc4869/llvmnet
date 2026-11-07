/* The C library's allocator under threads: 4,000 short-lived threads, 32 at a time, that allocate, grow, clear and
 * align blocks, free most of them and hand the rest to the main thread, which frees them after the thread has
 * exited; producer and consumer threads that free each other's blocks while both run; thread-specific data
 * destructors that free and allocate while their thread exits; detached threads still allocating when main returns.
 * Prints totals that must equal the native build's. */
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define THREADS 4000
#define BATCH 32
#define BLOCKS 64
#define KEPT 8

struct worker {
    unsigned index;
    unsigned char *kept[KEPT];
    size_t kept_sizes[KEPT];
    unsigned long checksum;
    int failures;
};

static unsigned long next(unsigned long *state)
{
    *state = *state * 6364136223846793005UL + 1442695040888963407UL;
    return *state >> 33;
}

/* Mostly small blocks, some of a few pages and a few of megabytes, as programs allocate. */
static size_t block_size(unsigned long *state)
{
    unsigned long r = next(state);
    if (r % 64 == 0) return 1 + next(state) % (3u << 20);
    if (r % 8 == 0) return 1 + next(state) % (256u << 10);
    return 1 + next(state) % 1024;
}

static void fill(unsigned char *p, size_t size, unsigned seed)
{
    for (size_t i = 0; i < size; i += 61) p[i] = (unsigned char)(seed + i);
    p[size - 1] = (unsigned char)seed;
}

static int intact(const unsigned char *p, size_t size, unsigned seed)
{
    for (size_t i = 0; i < size; i += 61)
        if (p[i] != (unsigned char)(seed + i) && i != size - 1) return 0;
    return p[size - 1] == (unsigned char)seed;
}

static void *short_lived(void *argument)
{
    struct worker *w = argument;
    unsigned long state = w->index + 1;
    unsigned char *blocks[BLOCKS];
    size_t sizes[BLOCKS];
    for (int i = 0; i < BLOCKS; i++) {
        sizes[i] = block_size(&state);
        blocks[i] = malloc(sizes[i]);
        if (!blocks[i]) { w->failures++; sizes[i] = 0; continue; }
        fill(blocks[i], sizes[i], w->index + i);
    }
    for (int i = 0; i < BLOCKS; i += 4) {
        if (!blocks[i]) continue;
        size_t grown = sizes[i] * 3 + 17;
        unsigned char *moved = realloc(blocks[i], grown);
        if (!moved || !intact(moved, sizes[i], w->index + i)) { w->failures++; continue; }
        blocks[i] = moved;
        sizes[i] = grown;
        fill(blocks[i], sizes[i], w->index + i);
    }
    size_t count = 1 + next(&state) % 5000;
    unsigned *zeroed = calloc(count, sizeof *zeroed);
    for (size_t i = 0; zeroed && i < count; i++) w->failures += zeroed[i] != 0;
    free(zeroed);
    size_t alignment = (size_t)16 << (w->index % 9);
    void *aligned = aligned_alloc(alignment, alignment * 3);
    w->failures += !aligned || (uintptr_t)aligned % alignment != 0;
    free(aligned);
    for (int i = 0; i < BLOCKS; i++) {
        if (!blocks[i]) continue;
        w->failures += !intact(blocks[i], sizes[i], w->index + i);
        w->checksum += sizes[i] + blocks[i][sizes[i] / 2 / 61 * 61];
        if (i < KEPT) { w->kept[i] = blocks[i]; w->kept_sizes[i] = sizes[i]; }
        else free(blocks[i]);
    }
    if (w->index % 2) pthread_exit(w);
    return w;
}

/* Producers hand blocks to consumers through a bounded queue, so most frees are on another, running thread. */
#define PRODUCERS 4
#define ITEMS 50000
#define QUEUE 256
static unsigned char *queue[QUEUE];
static size_t queue_sizes[QUEUE];
static unsigned head, tail, taken;
static unsigned long consumed_sum, consumed_count;
static int queue_failures;
static pthread_mutex_t queue_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t not_full = PTHREAD_COND_INITIALIZER, not_empty = PTHREAD_COND_INITIALIZER;

static void *producer(void *argument)
{
    unsigned long state = (uintptr_t)argument * 7919 + 3;
    for (int i = 0; i < ITEMS; i++) {
        size_t size = block_size(&state);
        unsigned char *p = malloc(size);
        if (p) fill(p, size, (unsigned)size);
        pthread_mutex_lock(&queue_lock);
        while (head - tail == QUEUE) pthread_cond_wait(&not_full, &queue_lock);
        queue[head % QUEUE] = p;
        queue_sizes[head % QUEUE] = size;
        head++;
        pthread_cond_signal(&not_empty);
        pthread_mutex_unlock(&queue_lock);
    }
    return 0;
}

static void *consumer(void *argument)
{
    (void)argument;
    for (;;) {
        pthread_mutex_lock(&queue_lock);
        while (head == tail && taken < PRODUCERS * ITEMS) pthread_cond_wait(&not_empty, &queue_lock);
        if (head == tail) { pthread_mutex_unlock(&queue_lock); return 0; }
        unsigned char *p = queue[tail % QUEUE];
        size_t size = queue_sizes[tail % QUEUE];
        tail++;
        taken++;
        if (taken == PRODUCERS * ITEMS) pthread_cond_broadcast(&not_empty);
        pthread_cond_signal(&not_full);
        pthread_mutex_unlock(&queue_lock);
        int bad = !p || !intact(p, size, (unsigned)size);
        free(p);
        pthread_mutex_lock(&queue_lock);
        consumed_sum += size;
        consumed_count++;
        queue_failures += bad;
        pthread_mutex_unlock(&queue_lock);
    }
}

/* Destructors of thread-specific data run while the thread exits, before or after the allocator's own. */
static pthread_key_t key;
static int destructor_runs;

static void destroy(void *value)
{
    free(value);
    char *late = malloc(100 + __atomic_fetch_add(&destructor_runs, 1, __ATOMIC_SEQ_CST) % 1000);
    if (late) memset(late, 1, 100);
    free(late);
}

static void *with_specific(void *argument)
{
    pthread_setspecific(key, malloc(64 + (uintptr_t)argument % 4096));
    return 0;
}

static void *detached(void *argument)
{
    unsigned long state = (uintptr_t)argument;
    for (;;) free(malloc(block_size(&state) % 4096 + 1));
    return 0;
}

int main(void)
{
    static struct worker workers[THREADS];
    unsigned long checksum = 0, kept = 0;
    int failures = 0;
    for (unsigned first = 0; first < THREADS; first += BATCH) {
        pthread_t threads[BATCH];
        for (unsigned i = 0; i < BATCH; i++) {
            workers[first + i].index = first + i;
            if (pthread_create(&threads[i], 0, short_lived, &workers[first + i])) return 1;
        }
        for (unsigned i = 0; i < BATCH; i++) pthread_join(threads[i], 0);
        for (unsigned i = 0; i < BATCH; i++) {
            struct worker *w = &workers[first + i];
            checksum += w->checksum;
            failures += w->failures;
            for (int k = 0; k < KEPT; k++) {
                if (!w->kept[k]) continue;
                failures += !intact(w->kept[k], w->kept_sizes[k], w->index + k);
                free(w->kept[k]);
                kept++;
            }
        }
    }
    printf("short-lived threads: %d, checksum %lu, blocks freed after exit %lu, failures %d\n", THREADS, checksum, kept, failures);

    pthread_t producers[PRODUCERS], consumers[PRODUCERS];
    for (uintptr_t i = 0; i < PRODUCERS; i++) {
        pthread_create(&producers[i], 0, producer, (void *)i);
        pthread_create(&consumers[i], 0, consumer, 0);
    }
    for (int i = 0; i < PRODUCERS; i++) pthread_join(producers[i], 0);
    for (int i = 0; i < PRODUCERS; i++) pthread_join(consumers[i], 0);
    printf("producers and consumers: %lu blocks, %lu bytes, failures %d\n", consumed_count, consumed_sum, queue_failures);

    pthread_key_create(&key, destroy);
    for (uintptr_t round = 0; round < 64; round++) {
        pthread_t threads[16];
        for (uintptr_t i = 0; i < 16; i++) pthread_create(&threads[i], 0, with_specific, (void *)(round * 16 + i));
        for (int i = 0; i < 16; i++) pthread_join(threads[i], 0);
    }
    printf("thread-specific destructors: %d\n", destructor_runs);

    for (uintptr_t i = 0; i < 8; i++) {
        pthread_t thread;
        pthread_attr_t attributes;
        pthread_attr_init(&attributes);
        pthread_attr_setdetachstate(&attributes, PTHREAD_CREATE_DETACHED);
        pthread_create(&thread, &attributes, detached, (void *)(i + 1));
        pthread_attr_destroy(&attributes);
    }
    unsigned long state = 99;
    for (int i = 0; i < 200000; i++) free(malloc(block_size(&state) % 4096 + 1));
    printf("detached threads still allocating at exit: 8\n");
    return 0;
}
