/* llvmnet port (NX-08): musl thread state for a thread that a host library created and that calls into the program
 * through a native extension's reverse thunk. Such a thread has no thread descriptor, so errno, locks, pthread_self and
 * thread-local storage would fault there. LlvmNet.Runtime calls __llvmnet_attach_thread on the thread before the first
 * callback runs on it: it builds a descriptor with the program's TLS and a TSD area as pthread_create does for a new
 * thread, makes it the thread pointer, and turns on locking as the first pthread_create does. The thread is detached
 * and not on musl's thread list: pthread_join and pthread_exit do not apply to it. Once the thread has ended, the
 * runtime calls __llvmnet_detach_thread from a finalizer, which only unmaps the memory and so needs no thread pointer.
 * Thread-local and pthread key destructors do not run for such a thread. The driver links these functions only into
 * images that use a native extension. */
#define _GNU_SOURCE
#include "pthread_impl.h"
#include "stdio_impl.h"
#include "libc.h"
#include "syscall.h"
#include <string.h>
#include <sys/mman.h>

static void dummy_0()
{
}
weak_alias(dummy_0, __membarrier_init);

/* pthread_key_create.c and the stdio streams override these, as for pthread_create. */
static volatile size_t dummy = 0;
weak_alias(dummy, __pthread_tsd_size);
static void *dummy_tsd[1] = { 0 };
weak_alias(dummy_tsd, __pthread_tsd_main);
static FILE *volatile dummy_file = 0;
weak_alias(dummy_file, __stdin_used);
weak_alias(dummy_file, __stdout_used);
weak_alias(dummy_file, __stderr_used);

static struct pthread *main_thread;

/* Runs on the main thread at start, before any other thread exists. */
__attribute__((constructor)) static void remember_main_thread(void)
{
	main_thread = __pthread_self();
}

static void init_file_lock(FILE *f)
{
	if (f && f->lock < 0) f->lock = 0;
}

/* The descriptor, now the calling thread's, or null when it cannot be made. */
void *__llvmnet_attach_thread(void)
{
	size_t size = (libc.tls_size + __pthread_tsd_size + PAGE_SIZE - 1) & -PAGE_SIZE;
	/* Raw system calls until the thread pointer is set: errno is not reachable before. */
	long map = __syscall(SYS_mmap, 0, size, PROT_READ|PROT_WRITE, MAP_PRIVATE|MAP_ANON, -1, 0);
	if (map < 0 && map > -4096) return 0;
	unsigned char *tsd = (unsigned char *)map + size - __pthread_tsd_size;
	struct pthread *td = __copy_tls(tsd - libc.tls_size);
	td->map_base = (void *)map;
	td->map_size = size;
	td->self = td;
	td->tsd = (void *)tsd;
	td->locale = &libc.global_locale;
	td->detach_state = DT_DETACHED;
	td->robust_list.head = &td->robust_list.head;
	if (main_thread) {
		td->canary = main_thread->canary;
		td->sysinfo = main_thread->sysinfo;
	}
	td->next = td->prev = td;
	if (__set_thread_area(TP_ADJ(td)) < 0) {
		__syscall(SYS_munmap, map, size);
		return 0;
	}
	td->tid = __syscall(SYS_gettid);
	/* From here on the thread is an ordinary musl thread. Another thread needs locking, which the first
	 * pthread_create turns on in a program that had only one. */
	__tl_lock();
	if (!libc.threaded) {
		for (FILE *f = *__ofl_lock(); f; f = f->next) init_file_lock(f);
		__ofl_unlock();
		init_file_lock(__stdin_used);
		init_file_lock(__stdout_used);
		init_file_lock(__stderr_used);
		if (main_thread) main_thread->tsd = (void **)__pthread_tsd_main;
		__membarrier_init();
		libc.threaded = 1;
	}
	libc.threads_minus_1++;
	libc.need_locks = 1;
	__tl_unlock();
	return td;
}

/* Unmaps the descriptor of an attached thread that has ended. Locking stays on. */
void __llvmnet_detach_thread(void *descriptor)
{
	struct pthread *td = descriptor;
	if (td && td->map_base) __syscall(SYS_munmap, td->map_base, td->map_size);
}
