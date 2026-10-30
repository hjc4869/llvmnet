/* llvmnet port of musl: memmove through the compiler builtin; see memcpy.c. */
#include <string.h>

void *memmove(void *dest, const void *src, size_t n)
{
	return __builtin_memmove(dest, src, n);
}
