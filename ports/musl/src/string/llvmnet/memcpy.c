/* llvmnet port of musl: memcpy through the compiler builtin, which the backend lowers to the runtime's vectorized
 * copy (Memory.Copy). Natively the builtin would call memcpy again; in a translated program it never does. */
#include <string.h>

void *memcpy(void *restrict dest, const void *restrict src, size_t n)
{
	return __builtin_memcpy(dest, src, n);
}
