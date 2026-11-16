/* llvmnet port of musl: memcmp eight bytes at a time. musl's loop compares single bytes, which a native build never
 * runs for long (glibc's memcmp is vector code) and which took 1.5% of a translated Clang's time (PF-30). The portable
 * target is little-endian, so the lowest differing byte of two words is the first that differs in memory. */
#include <string.h>
#include <stdint.h>

int memcmp(const void *vl, const void *vr, size_t n)
{
	const unsigned char *l=vl, *r=vr;
	for (; n >= 8; n -= 8, l += 8, r += 8) {
		uint64_t a, b;
		__builtin_memcpy(&a, l, 8);
		__builtin_memcpy(&b, r, 8);
		if (a != b) {
			int shift = __builtin_ctzll(a ^ b) & ~7;
			return (int)(a >> shift & 255) - (int)(b >> shift & 255);
		}
	}
	for (; n && *l == *r; n--, l++, r++);
	return n ? *l-*r : 0;
}
