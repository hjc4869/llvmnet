/* llvmnet port of musl: memset through the compiler builtin; see memcpy.c. */
#include <string.h>

void *memset(void *dest, int c, size_t n)
{
	return __builtin_memset(dest, c, n);
}
