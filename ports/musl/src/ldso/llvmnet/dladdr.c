/* llvmnet port of musl: dladdr describes the extension stubs dlsym returned (__llvmnet_dladdr): the library name the
 * program opened, its handle as the base, the symbol name and the address. Any other address, including the
 * program's own functions, is in no shared object of a portable image and returns 0, as musl's static dladdr does. */
#define _GNU_SOURCE
#include <dlfcn.h>

int __llvmnet_dladdr(const void *, Dl_info *);

int dladdr(const void *addr, Dl_info *info)
{
	return __llvmnet_dladdr(addr, info);
}
