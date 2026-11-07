/* The malloc family of the portable C library against native musl: results, alignment and errno of malloc, calloc,
 * realloc, free, aligned_alloc, posix_memalign, memalign, valloc, reallocarray and malloc_usable_size, including the
 * failures, and musl functions that allocate for the caller (strdup, getline, open_memstream). Built with
 * -fno-builtin, so the compiler cannot assume that a failing malloc leaves errno alone. */
#define _GNU_SOURCE
#include <errno.h>
#include <malloc.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int aligned(const void *p, size_t alignment) { return p && (uintptr_t)p % alignment == 0; }

int main(void)
{
    void *a = malloc(0), *b = malloc(0), *c = malloc(0);
    printf("malloc(0): distinct non-null %d\n", a && b && c && a != b && b != c && a != c);
    free(a); free(b); free(c);
    free(NULL);

    errno = EBADF;
    void *p = malloc(SIZE_MAX);
    printf("malloc(SIZE_MAX): %s, ENOMEM %d\n", p ? "pointer" : "null", errno == ENOMEM);
    errno = EBADF;
    p = malloc(SIZE_MAX / 2);
    printf("malloc(SIZE_MAX / 2): %s, ENOMEM %d\n", p ? "pointer" : "null", errno == ENOMEM);
    errno = 0;
    p = calloc(SIZE_MAX / 2, 4);
    printf("calloc overflow: %s, ENOMEM %d\n", p ? "pointer" : "null", errno == ENOMEM);

    unsigned char *zero = calloc(1 << 20, 3);
    int nonzero = 0;
    for (size_t i = 0; zero && i < 3u << 20; i++) nonzero += zero[i] != 0;
    printf("calloc(3 MiB): zero %d\n", zero && !nonzero);
    free(zero);
    for (int round = 0; round < 3; round++) {
        unsigned char *dirty = malloc(4096);
        memset(dirty, 0xa5, 4096);
        free(dirty);
        unsigned char *again = calloc(1, 4096);
        nonzero = 0;
        for (int i = 0; again && i < 4096; i++) nonzero += again[i] != 0;
        printf("calloc after free: zero %d\n", again && !nonzero);
        free(again);
    }

    char *text = realloc(NULL, 10);
    strcpy(text, "realloc");
    text = realloc(text, 1 << 20);
    printf("realloc grow: %s\n", text);
    text = realloc(text, 4);
    printf("realloc shrink: %.3s\n", text);
    void *empty = realloc(text, 0);
    printf("realloc(p, 0): %s\n", empty ? "pointer" : "null");
    free(empty);

    errno = 0;
    p = aligned_alloc(3, 8);
    printf("aligned_alloc(3): %s, EINVAL %d\n", p ? "pointer" : "null", errno == EINVAL);
    p = aligned_alloc(0, 8);
    printf("aligned_alloc(0): %s\n", p ? "pointer" : "null");
    free(p);
    for (size_t alignment = 1; alignment <= (size_t)1 << 22; alignment <<= 3) {
        p = aligned_alloc(alignment, 100);
        printf("aligned_alloc(%zu): aligned %d\n", alignment, aligned(p, alignment));
        free(p);
    }
    p = (void *)1;
    printf("posix_memalign(4): %d unchanged %d\n", posix_memalign(&p, 4, 8) == EINVAL, p == (void *)1);
    printf("posix_memalign(24): %d\n", posix_memalign(&p, 24, 8) == EINVAL);
    printf("posix_memalign(64): %d aligned %d\n", posix_memalign(&p, 64, 100), aligned(p, 64));
    free(p);
    p = memalign(4096, 10);
    printf("memalign(4096): aligned %d\n", aligned(p, 4096));
    free(p);
    p = valloc(10);
    printf("valloc: aligned %d\n", aligned(p, 4096));
    free(p);

    errno = 0;
    p = reallocarray(NULL, SIZE_MAX / 2, 3);
    printf("reallocarray overflow: %s, ENOMEM %d\n", p ? "pointer" : "null", errno == ENOMEM);
    p = reallocarray(NULL, 100, 8);
    printf("reallocarray: %s\n", p ? "pointer" : "null");
    free(p);

    printf("malloc_usable_size(NULL): %zu\n", malloc_usable_size(NULL));
    int covered = 1;
    for (size_t size = 1; size < (64u << 20); size = size * 3 + 1) {
        p = malloc(size);
        covered &= p && malloc_usable_size(p) >= size;
        free(p);
    }
    printf("malloc_usable_size covers the request: %d\n", covered);

    char *copy = strdup("strdup"), *part = strndup("strndup", 4);
    printf("%s %s\n", copy, part);
    free(copy);
    free(part);
    char *buffer = 0;
    size_t length = 0;
    FILE *stream = open_memstream(&buffer, &length);
    for (int i = 0; i < 10000; i++) fprintf(stream, "%d\n", i);
    fclose(stream);
    printf("open_memstream: %zu bytes\n", length);
    FILE *lines = fmemopen(buffer, length, "r");
    char *line = 0;
    size_t capacity = 0;
    long count = 0, sum = 0;
    while (getline(&line, &capacity, lines) > 0) { count++; sum += atol(line); }
    fclose(lines);
    free(line);
    free(buffer);
    printf("getline: %ld lines, sum %ld\n", count, sum);

    enum { COUNT = 200000 };
    static void *blocks[COUNT];
    unsigned long state = 1;
    for (int i = 0; i < COUNT; i++) {
        state = state * 6364136223846793005UL + 1442695040888963407UL;
        blocks[i] = malloc(1 + (state >> 33) % 2000);
    }
    for (int i = 0; i < COUNT; i++) {
        state = state * 6364136223846793005UL + 1442695040888963407UL;
        int j = (int)((state >> 33) % COUNT);
        free(blocks[j]);
        blocks[j] = 0;
    }
    for (int i = 0; i < COUNT; i++) free(blocks[i]);
    p = malloc(256u << 20);
    if (p) memset(p, 1, 256u << 20);
    printf("200000 blocks freed in random order, then 256 MiB: %s\n", p ? "pointer" : "null");
    free(p);
    return 0;
}
