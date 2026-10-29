/* Under-aligned (packed) and 128-bit atomics: clang emits IR atomics that native backends lower to libatomic calls. */
#include <stdint.h>
#include <stdio.h>
struct __attribute__((packed)) packed { char tag; uint32_t value; };
static struct packed p;
static __int128 wide;
int main(void) {
    __atomic_fetch_add(&p.value, 5, __ATOMIC_SEQ_CST);
    __int128 expected = 0;
    __atomic_compare_exchange_n(&wide, &expected, (__int128)1 << 100, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    __int128 loaded = __atomic_load_n(&wide, __ATOMIC_SEQ_CST);
    printf("%u %d\n", p.value, (int)(loaded >> 100));
    return 0;
}
