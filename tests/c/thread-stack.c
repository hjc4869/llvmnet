/* Thread stacks: a thread gets the stack size pthread_attr_setstacksize asks for, a default thread at least Linux's
 * usual 8 MB (the test runs under a 16 MB stack limit, which glibc uses as its default), and getrlimit(RLIMIT_STACK)
 * reports the process's limit. The output must match the native build's. */
#include <alloca.h>
#include <pthread.h>
#include <stdio.h>
#include <string.h>
#include <sys/resource.h>

static size_t megabytes;

static void *worker(void *unused)
{
    char *block = alloca(megabytes << 20);
    memset(block, 1, megabytes << 20);
    return (void *)(long)block[(megabytes << 20) - 1];
}

static void run(const char *label, size_t use, size_t request)
{
    pthread_attr_t attributes;
    pthread_t thread;
    void *result = 0;
    megabytes = use;
    pthread_attr_init(&attributes);
    if (request) pthread_attr_setstacksize(&attributes, request << 20);
    if (pthread_create(&thread, &attributes, worker, 0)) { printf("%s: create failed\n", label); return; }
    pthread_join(thread, &result);
    printf("%s: %zu MB used on a thread (requested %zu MB): %ld\n", label, use, request, (long)result);
}

int main(void)
{
    struct rlimit limit;
    getrlimit(RLIMIT_STACK, &limit);
    printf("stack limit %lu KB\n", (unsigned long)(limit.rlim_cur >> 10));
    run("default", 6, 0);
    run("explicit", 48, 64);
    run("small", 2, 4);
    return 0;
}
