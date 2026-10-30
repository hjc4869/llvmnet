/* llvmnet port of musl: thread cancellation without the assembly cancellation-point window (__cp_begin,
 * __cp_end and __cp_cancel in src/thread/x86_64/syscall_cp.s). pthread_cancel marks the target and sends it
 * SIGCANCEL; the host does not run a handler for it but interrupts the target's blocking system call with
 * -EINTR (src/LlvmNet.Runtime/Host/HostThreads.cs). A cancellation point acts on a request it finds before
 * the call or after an interrupted call, which is what the window achieves natively. Asynchronous
 * cancellation takes effect at cancellation points only. Otherwise as upstream src/thread/pthread_cancel.c. */
#define _GNU_SOURCE
#include <string.h>
#include "pthread_impl.h"
#include "syscall.h"

hidden long __cancel(), __syscall_cp_c();

long __cancel()
{
	pthread_t self = __pthread_self();
	if (self->canceldisable == PTHREAD_CANCEL_ENABLE || self->cancelasync)
		pthread_exit(PTHREAD_CANCELED);
	self->canceldisable = PTHREAD_CANCEL_DISABLE;
	return -ECANCELED;
}

long __syscall_cp_c(syscall_arg_t nr,
                    syscall_arg_t u, syscall_arg_t v, syscall_arg_t w,
                    syscall_arg_t x, syscall_arg_t y, syscall_arg_t z)
{
	pthread_t self;
	long r;
	int st;

	if ((st=(self=__pthread_self())->canceldisable)
	    && (st==PTHREAD_CANCEL_DISABLE || nr==SYS_close))
		return __syscall(nr, u, v, w, x, y, z);

	if (self->cancel && nr!=SYS_close)
		return __cancel();
	r = __syscall(nr, u, v, w, x, y, z);
	if (r==-EINTR && nr!=SYS_close && self->cancel &&
	    self->canceldisable != PTHREAD_CANCEL_DISABLE)
		r = __cancel();
	return r;
}

void __testcancel()
{
	pthread_t self = __pthread_self();
	if (self->cancel && !self->canceldisable)
		__cancel();
}

int pthread_cancel(pthread_t t)
{
	a_store(&t->cancel, 1);
	if (t == pthread_self()) {
		if (t->canceldisable == PTHREAD_CANCEL_ENABLE && t->cancelasync)
			pthread_exit(PTHREAD_CANCELED);
		return 0;
	}
	return pthread_kill(t, SIGCANCEL);
}
