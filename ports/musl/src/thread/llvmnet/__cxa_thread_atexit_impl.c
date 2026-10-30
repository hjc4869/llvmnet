/* llvmnet port of musl: the C++ thread_local destructor registry that glibc provides and musl 1.2.5 lacks.
 * libc++abi is built with HAVE___CXA_THREAD_ATEXIT_IMPL, so __cxa_thread_atexit calls this function.
 * Destructors run in reverse order of registration: for the main thread at the start of exit(), before
 * atexit handlers and static destructors, and for other threads when they exit. */
#include <stdlib.h>
#include <pthread.h>
#include "libc.h"

struct thread_dtor {
	void (*function)(void *);
	void *object;
	struct thread_dtor *next;
};

static __thread struct thread_dtor *thread_dtors;
static pthread_key_t thread_dtor_key;
static pthread_once_t thread_dtor_once = PTHREAD_ONCE_INIT;

static void run_thread_dtors(void *unused)
{
	struct thread_dtor *head;
	while ((head = thread_dtors)) {
		thread_dtors = head->next;
		head->function(head->object);
		free(head);
	}
}

static void create_key(void)
{
	pthread_key_create(&thread_dtor_key, run_thread_dtors);
}

int __cxa_thread_atexit_impl(void (*function)(void *), void *object, void *dso)
{
	struct thread_dtor *head = malloc(sizeof *head);
	if (!head) return -1;
	/* Any non-null value makes the key destructor run when a thread other than the one calling exit ends. */
	pthread_once(&thread_dtor_once, create_key);
	pthread_setspecific(thread_dtor_key, &thread_dtor_key);
	head->function = function;
	head->object = object;
	head->next = thread_dtors;
	thread_dtors = head;
	return 0;
}

hidden void __llvmnet_thread_dtors_on_exit(void)
{
	run_thread_dtors(0);
}
