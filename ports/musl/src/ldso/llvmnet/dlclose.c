/* llvmnet port of musl: dlclose checks the handle with the host (__llvmnet_dlclose). Libraries stay loaded, as in
 * musl, so a later dlopen of the same name returns the same handle. */
#include <dlfcn.h>
#include "dynlink.h"

int __llvmnet_dlclose(void *);
hidden void __dl_seterr_host(void);

int dlclose(void *p)
{
	int result = __llvmnet_dlclose(p);
	if (result) __dl_seterr_host();
	return result;
}
