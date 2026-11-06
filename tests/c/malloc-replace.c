/* A program that replaces the allocator, as musl lets a static program do: its own malloc, calloc, realloc, free and
 * aligned_alloc serve the program and musl's functions that allocate for the caller (strdup, getline, fopen), and it
 * defines names of mimalloc's own, which the C library's allocator must not clash with. Must print what the native
 * musl build prints. */
#define _GNU_SOURCE
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static unsigned char arena[1 << 22];
static size_t used;
static int allocations, releases;

static size_t *header(void *p) { return (size_t *)p - 2; }

void *malloc(size_t n)
{
    n = (n + 15) & ~(size_t)15;
    if (used + n + 16 > sizeof arena) return 0;
    void *p = arena + used + 16;
    *header(p) = n;
    used += n + 16;
    allocations++;
    return p;
}

void free(void *p)
{
    if (p) releases++;
}

void *calloc(size_t m, size_t n)
{
    void *p = malloc(m * n);
    if (p) memset(p, 0, m * n);
    return p;
}

void *realloc(void *p, size_t n)
{
    void *q = malloc(n);
    if (p && q) memcpy(q, p, *header(p) < n ? *header(p) : n);
    free(p);
    return q;
}

void *aligned_alloc(size_t alignment, size_t n)
{
    (void)alignment;
    return malloc(n);
}

/* Names of mimalloc's, as a program that links a mimalloc of its own has them. */
int mi_version(void) { return 1; }
void *mi_malloc(size_t n) { return malloc(n); }
long mi_option_get(int option) { return option; }

static int ours(const void *p) { return (const unsigned char *)p >= arena && (const unsigned char *)p < arena + sizeof arena; }

int main(void)
{
    char *copy = strdup("strdup");
    char *line = 0;
    size_t capacity = 0;
    FILE *stream = fmemopen("a line\nanother line\n", 20, "r");
    long lines = 0;
    while (getline(&line, &capacity, stream) > 0) lines++;
    fclose(stream);
    void *own = mi_malloc(100);
    printf("strdup %d, getline %d (%ld lines), mi_malloc %d, mi_version %d, mi_option_get %ld\n", ours(copy), ours(line),
           lines, ours(own), mi_version(), mi_option_get(5));
    free(copy);
    free(line);
    free(own);
    printf("the program's allocator served %s and released %s\n", allocations > 0 ? "blocks" : "nothing",
           releases > 0 ? "blocks" : "nothing");
    return 0;
}
