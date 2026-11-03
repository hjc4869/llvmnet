/* llvmnet port of musl: dlsym through the host (__llvmnet_dlsym), which returns the managed function pointer of the
 * extension's stub for the symbol, callable from translated code like any function pointer. RTLD_DEFAULT and
 * RTLD_NEXT search the libraries opened with RTLD_GLOBAL; the program's own symbols are not searched. */
#include <dlfcn.h>
#include "dynlink.h"

void *__llvmnet_dlsym(void *, const char *);
hidden void __dl_seterr_host(void);

void *__dlsym(void *restrict p, const char *restrict s, void *restrict ra)
{
	void *symbol = __llvmnet_dlsym(p, s);
	if (!symbol) __dl_seterr_host();
	return symbol;
}
