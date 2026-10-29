/* llvmnet port of musl: threads are managed threads created by the host. The host honors
 * CLONE_SETTLS, CLONE_PARENT_SETTID and CLONE_CHILD_CLEARTID as Linux does, which is all
 * pthread_create relies on; the stack argument is ignored because .NET threads have their own. */
#include <stdarg.h>
#include "pthread_impl.h"

int __llvmnet_clone(int (*)(void *), void *, int, int *, void *, int *);

int __clone(int (*func)(void *), void *stack, int flags, void *arg, ...)
{
	va_list ap;
	va_start(ap, arg);
	int *ptid = va_arg(ap, int *);
	void *tls = va_arg(ap, void *);
	int *ctid = va_arg(ap, int *);
	va_end(ap);
	return __llvmnet_clone(func, arg, flags, ptid, tls, ctid);
}
