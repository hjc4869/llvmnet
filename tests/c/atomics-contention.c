/* Multi-threaded atomic read-modify-write on every width and operation (BE-16). Each thread applies a
 * deterministic sequence, so final values are exact; misaligned and adjacent sub-word locations check
 * that neighbouring bytes are never clobbered. Native clang is the oracle. */
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#define THREADS 8
#define ITERATIONS 20000
struct shared {
    uint8_t bytes[4];               /* four adjacent sub-word counters */
    uint16_t halves[2];
    uint32_t word_add, word_or, word_max, word_xor;
    uint64_t wide_add, wide_and, wide_min, wide_nand_count;
    int64_t signed_max, signed_min;
    float float_sum;
    double double_sum;
    uint64_t cas_counter;
    uint32_t flag;
    unsigned __int128 wide128;
} state;
static void *worker(void *argument)
{
    uintptr_t id = (uintptr_t)argument;
    for (int i = 0; i < ITERATIONS; i++) {
        __atomic_fetch_add(&state.bytes[id % 4], 1, __ATOMIC_RELAXED);
        __atomic_fetch_add(&state.halves[id % 2], 3, __ATOMIC_ACQ_REL);
        __atomic_fetch_add(&state.word_add, 7, __ATOMIC_SEQ_CST);
        __atomic_fetch_or(&state.word_or, 1u << (id * 4 + (i % 4)), __ATOMIC_RELAXED);
        __atomic_fetch_xor(&state.word_xor, (uint32_t)(i * 2654435761u + id), __ATOMIC_RELAXED);
        uint32_t candidate = (uint32_t)(id * ITERATIONS + i);
        __atomic_fetch_max(&state.word_max, candidate, __ATOMIC_RELAXED);
        __atomic_fetch_add(&state.wide_add, 0x100000001ull, __ATOMIC_SEQ_CST);
        __atomic_fetch_and(&state.wide_and, ~(1ull << ((id * 7 + i) % 64)), __ATOMIC_RELAXED);
        __atomic_fetch_min(&state.wide_min, (uint64_t)(1000000 + id * 31 + i), __ATOMIC_RELAXED);
        __atomic_fetch_max(&state.signed_max, (int64_t)(i - 5000) * (int64_t)(id + 1), __ATOMIC_RELAXED);
        __atomic_fetch_min(&state.signed_min, -(int64_t)i * (int64_t)(id + 1), __ATOMIC_RELAXED);
        __atomic_fetch_add(&state.float_sum, 0.5f, __ATOMIC_RELAXED);
        __atomic_fetch_add(&state.double_sum, 0.25, __ATOMIC_RELAXED);
        __atomic_fetch_add(&state.wide128, ((unsigned __int128)1 << 64) | 3, __ATOMIC_SEQ_CST);
        uint64_t expected = __atomic_load_n(&state.cas_counter, __ATOMIC_ACQUIRE);
        while (!__atomic_compare_exchange_n(&state.cas_counter, &expected, expected + 2, 1, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) { }
        /* Test-and-set spin lock protecting a non-atomic counter. */
        while (__atomic_exchange_n(&state.flag, 1u, __ATOMIC_ACQUIRE)) { }
        state.wide_nand_count++;
        __atomic_store_n(&state.flag, 0u, __ATOMIC_RELEASE);
    }
    return 0;
}
int main(void)
{
    memset(&state, 0, sizeof state);
    state.wide_and = ~0ull;
    state.wide_min = ~0ull;
    state.signed_max = INT64_MIN;
    state.signed_min = INT64_MAX;
    pthread_t threads[THREADS];
    for (uintptr_t id = 0; id < THREADS; id++) pthread_create(&threads[id], 0, worker, (void *)id);
    for (int id = 0; id < THREADS; id++) pthread_join(threads[id], 0);
    printf("bytes %u %u %u %u\n", state.bytes[0], state.bytes[1], state.bytes[2], state.bytes[3]);
    printf("halves %u %u\n", state.halves[0], state.halves[1]);
    printf("word add=%u or=%#x max=%u xor=%#x\n", state.word_add, state.word_or, state.word_max, state.word_xor);
    printf("wide add=%#llx and=%#llx min=%llu\n", (unsigned long long)state.wide_add, (unsigned long long)state.wide_and, (unsigned long long)state.wide_min);
    printf("signed max=%lld min=%lld\n", (long long)state.signed_max, (long long)state.signed_min);
    printf("floating %.1f %.2f\n", state.float_sum, state.double_sum);
    printf("wide128 high=%llu low=%llu\n", (unsigned long long)(state.wide128 >> 64), (unsigned long long)state.wide128);
    printf("cas %llu locked %llu\n", (unsigned long long)state.cas_counter, (unsigned long long)state.wide_nand_count);
    return 0;
}
