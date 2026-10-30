/* llvmnet port of musl: upstream src/exit/exit.c with one addition. exit() first runs the calling thread's
 * C++ thread_local destructors, as glibc's exit does (plan item CX-12); they are registered through
 * __cxa_thread_atexit_impl in src/thread/llvmnet/__cxa_thread_atexit_impl.c, which overrides the hook. */
#include <stdlib.h>
#include <stdint.h>
#include "libc.h"

static void dummy()
{
}

/* atexit.c and __stdio_exit.c override these. the latter is linked
 * as a consequence of linking either __toread.c or __towrite.c. */
weak_alias(dummy, __funcs_on_exit);
weak_alias(dummy, __stdio_exit);
weak_alias(dummy, _fini);
weak_alias(dummy, __llvmnet_thread_dtors_on_exit);

extern weak hidden void (*const __fini_array_start)(void), (*const __fini_array_end)(void);

static void libc_exit_fini(void)
{
	uintptr_t a = (uintptr_t)&__fini_array_end;
	for (; a>(uintptr_t)&__fini_array_start; a-=sizeof(void(*)()))
		(*(void (**)())(a-sizeof(void(*)())))();
	_fini();
}

weak_alias(libc_exit_fini, __libc_exit_fini);

_Noreturn void exit(int code)
{
	__llvmnet_thread_dtors_on_exit();
	__funcs_on_exit();
	__libc_exit_fini();
	__stdio_exit();
	_Exit(code);
}
