/* llvmnet port of mimalloc: the C library's allocator. This translation unit is all of mimalloc (its single-file
 * src/static.c) and the allocation functions of musl's libc.a, which the sysroot leaves out of musl (mallocng and
 * its wrappers in src/malloc): the C names, musl's malloc_usable_size and the internal names other musl sources call,
 * as aliases of mimalloc's functions, or with musl's argument checks where mimalloc's results or errno differ.
 * musl's posix_memalign, memalign, valloc and reallocarray stay and call aligned_alloc and realloc here; C++'s
 * operator new and delete stay libc++abi's, which call malloc and free.
 *
 * The C names are weak: in musl each is an archive member of its own, which a program that defines its own malloc,
 * calloc or free keeps out of the link, and in this one object only a weak definition gives way to the program's.
 * musl's internal names stay mimalloc's, as they stay mallocng's in musl. scripts/build-portable-mimalloc.sh makes
 * every other symbol of the object internal, so mimalloc's own names cannot clash with a program's. */
#include "static.c"

/* musl's check: a size that overflows fails with ENOMEM, which mimalloc's release build does not set. */
static void *musl_calloc(size_t m, size_t n)
{
	if (n && m > (size_t)-1/n) {
		errno = ENOMEM;
		return 0;
	}
	return mi_calloc(m, n);
}

/* musl's checks: an alignment that is not a power of two fails with EINVAL, 0 asks for none, and any other failure
 * sets ENOMEM, which mimalloc does not for a size beyond PTRDIFF_MAX. */
static void *musl_aligned_alloc(size_t align, size_t len)
{
	if ((align & -align) != align) {
		errno = EINVAL;
		return 0;
	}
	void *p = mi_malloc_aligned(len, align ? align : 1);
	if (!p) errno = ENOMEM;
	return p;
}

void *malloc(size_t n) __attribute__((weak, alias("mi_malloc")));
void *calloc(size_t m, size_t n) __attribute__((weak, alias("musl_calloc")));
void *realloc(void *p, size_t n) __attribute__((weak, alias("mi_realloc")));
void free(void *p) __attribute__((weak, alias("mi_free")));
void *aligned_alloc(size_t align, size_t len) __attribute__((weak, alias("musl_aligned_alloc")));
size_t malloc_usable_size(void *p) __attribute__((weak, alias("mi_malloc_usable_size")));

/* musl's own callers of the allocator (locale, atexit, time zones, pthread_atfork, sem_open, dlerror, aio). */
void *__libc_malloc(size_t n) __attribute__((alias("mi_malloc")));
void *__libc_calloc(size_t m, size_t n) __attribute__((alias("musl_calloc")));
void *__libc_realloc(void *p, size_t n) __attribute__((alias("mi_realloc")));
void __libc_free(void *p) __attribute__((alias("mi_free")));

/* musl's dynamic linker gives the allocator the unused ends of loaded segments; portable programs have none. */
void __malloc_donate(char *start, char *end)
{
	(void)start;
	(void)end;
}
