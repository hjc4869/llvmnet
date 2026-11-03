/* llvmnet port of musl: dlopen opens a library that a native extension of the program serves (ADR-15). The host
 * (__llvmnet_dlopen in src/LlvmNet.Runtime/Host/HostDynamicLoading.cs) matches the file name against the libraries
 * of the extensions the image registered, loads the host library behind it, and returns a handle; any other name
 * fails with musl's message for static programs plus the name. dlopen(NULL) is not supported: a portable program's
 * own symbols cannot be looked up. Every RTLD_* flag is accepted; libraries are never unloaded, as in musl. */
#include <dlfcn.h>
#include "dynlink.h"

void *__llvmnet_dlopen(const char *, int);
hidden void __dl_seterr_host(void);

void *dlopen(const char *file, int mode)
{
	void *handle = __llvmnet_dlopen(file, mode);
	if (!handle) __dl_seterr_host();
	return handle;
}
