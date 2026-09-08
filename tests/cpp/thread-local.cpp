#include <pthread.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

static int anchor = 91;
static thread_local int scalar = 17;
static thread_local int destruction_order;
static int constructed;
static int destroyed;
static int failures;
static int ready;
static int release_workers;
static pthread_mutex_t mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t condition = PTHREAD_COND_INITIALIZER;

struct Data {
    long value;
    const char *text;
    int *pointer;
};

alignas(64) static thread_local Data data = {23, "tls", &anchor};

struct Object {
    int identifier;
    explicit Object(int value) : identifier(value) { __atomic_fetch_add(&constructed, 1, __ATOMIC_SEQ_CST); }
    ~Object() {
        destruction_order = destruction_order * 10 + identifier;
        if (identifier == 1 && destruction_order != 21)
            __atomic_fetch_add(&failures, 1, __ATOMIC_SEQ_CST);
        __atomic_fetch_add(&destroyed, 1, __ATOMIC_SEQ_CST);
    }
};

static thread_local Object first(1);
static thread_local Object second(2);

static void main_cleanup()
{
    if (destroyed != 12 || failures != 0 || destruction_order != 21)
        std::abort();
}

__attribute__((noinline)) static void throw_after_tls()
{
    if (first.identifier != 1 || second.identifier != 2)
        __atomic_fetch_add(&failures, 1, __ATOMIC_SEQ_CST);
    throw 42;
}

static void *exception_worker(void *)
{
    try {
        throw_after_tls();
    } catch (int value) {
        if (value != 42)
            __atomic_fetch_add(&failures, 1, __ATOMIC_SEQ_CST);
    }
    return 0;
}

static void *worker(void *argument)
{
    long identifier = reinterpret_cast<long>(argument);
    if (scalar != 17 || data.value != 23 || data.text[0] != 't' || *data.pointer != 91 ||
        reinterpret_cast<std::uintptr_t>(&data) % 64 != 0 || first.identifier != 1 || second.identifier != 2)
        __atomic_fetch_add(&failures, 1, __ATOMIC_SEQ_CST);
    scalar = static_cast<int>(identifier);
    data.value = identifier * 19;
    pthread_mutex_lock(&mutex);
    ++ready;
    pthread_cond_broadcast(&condition);
    while (!release_workers)
        pthread_cond_wait(&condition, &mutex);
    pthread_mutex_unlock(&mutex);
    if (scalar != identifier || data.value != identifier * 19)
        __atomic_fetch_add(&failures, 1, __ATOMIC_SEQ_CST);
    return argument;
}

int main()
{
    pthread_t threads[4];
    for (long index = 0; index < 4; ++index)
        if (pthread_create(&threads[index], 0, worker, reinterpret_cast<void *>(index + 1)))
            return 1;
    pthread_mutex_lock(&mutex);
    while (ready != 4)
        pthread_cond_wait(&condition, &mutex);
    if (scalar != 17 || data.value != 23 || constructed != 8)
        ++failures;
    release_workers = 1;
    pthread_cond_broadcast(&condition);
    pthread_mutex_unlock(&mutex);
    for (long index = 0; index < 4; ++index) {
        void *result = 0;
        if (pthread_join(threads[index], &result) || reinterpret_cast<long>(result) != index + 1)
            return 2;
    }
    if (failures || destroyed != 8)
        return 3;
    pthread_t exceptional;
    if (pthread_create(&exceptional, 0, exception_worker, 0) || pthread_join(exceptional, 0) || destroyed != 10)
        return 4;
    if (first.identifier != 1 || second.identifier != 2 || constructed != 12 || std::atexit(main_cleanup))
        return 5;
    std::puts("thread-local initialization, isolation, alignment and destructors passed");
    return 0;
}