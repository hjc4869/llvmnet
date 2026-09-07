#include <stdatomic.h>
#include <stdint.h>
static atomic_int counter;
static _Atomic uint64_t wide;
static atomic_uchar byte;
int main(void)
{
    atomic_store_explicit(&counter, 42, memory_order_release);
    if (atomic_load_explicit(&counter, memory_order_acquire) != 42) return 1;
    if (atomic_fetch_add(&counter, 3) != 42 || atomic_fetch_sub(&counter, 5) != 45) return 2;
    int expected = 40;
    if (!atomic_compare_exchange_strong(&counter, &expected, -7)) return 3;
    expected = 10;
    if (atomic_compare_exchange_weak(&counter, &expected, 99) || expected != -7) return 4;
    if (atomic_exchange(&counter, 1) != -7) return 5;
    atomic_store(&wide, UINT64_MAX - 3);
    if (atomic_fetch_add(&wide, 7) != UINT64_MAX - 3 || atomic_load(&wide) != 3) return 6;
    atomic_store(&byte, 255);
    if (atomic_fetch_add(&byte, 2) != 255 || atomic_load(&byte) != 1) return 7;
    atomic_thread_fence(memory_order_seq_cst);
    return 0;
}