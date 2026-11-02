/* llvmnet port of musl: dlerror keeps musl's per-thread message buffer. __dl_seterr_host copies into it the message
 * of the host call that just failed on this thread (__llvmnet_dlerror), so the dlopen, dlsym and dlclose ports report
 * the host's reason with musl's semantics: the most recent error of the thread, cleared by reading it. */
#include <dlfcn.h>
#include <stdlib.h>
#include <stdarg.h>
#include "pthread_impl.h"
#include "dynlink.h"
#include "atomic.h"

#define malloc __libc_malloc
#define calloc __libc_calloc
#define realloc __libc_realloc
#define free __libc_free

size_t __llvmnet_dlerror(char *, size_t);

char *dlerror()
{
	pthread_t self = __pthread_self();
	if (!self->dlerror_flag) return 0;
	self->dlerror_flag = 0;
	char *s = self->dlerror_buf;
	if (s == (void *)-1)
		return "Dynamic linker failed to allocate memory for error message";
	else
		return s;
}

/* Atomic singly-linked list, used to store list of thread-local dlerror
 * buffers for deferred free. They cannot be freed at thread exit time
 * because, by the time it's known they can be freed, the exiting thread
 * is in a highly restrictive context where it cannot call (even the
 * libc-internal) free. It also can't take locks; thus the atomic list. */

static void *volatile freebuf_queue;

void __dl_thread_cleanup(void)
{
	pthread_t self = __pthread_self();
	if (!self->dlerror_buf || self->dlerror_buf == (void *)-1)
		return;
	void *h;
	do {
		h = freebuf_queue;
		*(void **)self->dlerror_buf = h;
	} while (a_cas_p(&freebuf_queue, h, self->dlerror_buf) != h);
}

/* Frees the buffers of exited threads and this thread's previous message, and returns a buffer for a message of len
 * bytes, or (void *)-1 when there is no memory. */
static char *message_buffer(size_t len)
{
	void **q;
	do q = freebuf_queue;
	while (q && a_cas_p(&freebuf_queue, q, 0) != q);

	while (q) {
		void **p = *q;
		free(q);
		q = p;
	}

	pthread_t self = __pthread_self();
	if (self->dlerror_buf != (void *)-1)
		free(self->dlerror_buf);
	if (len < sizeof(void *)) len = sizeof(void *);
	char *buf = malloc(len+1);
	return buf ? buf : (void *)-1;
}

static void set_message(char *buf)
{
	pthread_t self = __pthread_self();
	self->dlerror_buf = buf;
	self->dlerror_flag = 1;
}

hidden void __dl_vseterr(const char *fmt, va_list ap)
{
	va_list ap2;
	va_copy(ap2, ap);
	size_t len = vsnprintf(0, 0, fmt, ap2);
	va_end(ap2);
	char *buf = message_buffer(len);
	if (buf != (void *)-1)
		vsnprintf(buf, len+1, fmt, ap);
	set_message(buf);
}

hidden void __dl_seterr(const char *fmt, ...)
{
	va_list ap;
	va_start(ap, fmt);
	__dl_vseterr(fmt, ap);
	va_end(ap);
}

hidden void __dl_seterr_host(void)
{
	size_t len = __llvmnet_dlerror(0, 0);
	char *buf = message_buffer(len);
	if (buf != (void *)-1)
		__llvmnet_dlerror(buf, len+1);
	set_message(buf);
}

/* dlinfo's handle check: the host's handles are not link maps, so dlinfo fails for every handle, as before. */
static int stub_invalid_handle(void *h)
{
	__dl_seterr("Invalid library handle %p", (void *)h);
	return 1;
}

weak_alias(stub_invalid_handle, __dl_invalid_handle);
