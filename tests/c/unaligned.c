/* Under-aligned and volatile memory access: every scalar width at every offset 0..7, packed
 * structures, memcpy/memmove with overlap, and volatile accesses. Native clang is the oracle. */
#include <stdint.h>
#include <stdio.h>
#include <string.h>
typedef uint16_t u16a __attribute__((aligned(1)));
typedef uint32_t u32a __attribute__((aligned(1)));
typedef uint64_t u64a __attribute__((aligned(1)));
typedef float f32a __attribute__((aligned(1)));
typedef double f64a __attribute__((aligned(1)));
typedef void *ptra __attribute__((aligned(1)));
typedef unsigned __int128 u128a __attribute__((aligned(1)));
typedef long double ldbla __attribute__((aligned(1)));
struct __attribute__((packed)) record { char tag; uint64_t wide; uint16_t half; double real; char tail; long double extended; };
static unsigned char buffer[96];
static uint64_t checksum(void) { uint64_t h = 1469598103934665603ull; for (size_t i = 0; i < sizeof buffer; i++) h = (h ^ buffer[i]) * 1099511628211ull; return h; }
int main(void)
{
    for (int offset = 0; offset < 8; offset++) {
        memset(buffer, 0xa5, sizeof buffer);
        unsigned char *p = buffer + offset;
        *(u16a *)(p + 1) = 0x1234; *(u32a *)(p + 3) = 0x89abcdefu; *(u64a *)(p + 7) = 0x0123456789abcdefull;
        *(f32a *)(p + 17) = 1.5f; *(f64a *)(p + 21) = -2.25; *(ptra *)(p + 29) = (void *)(uintptr_t)0x1122334455667788ull;
        *(u128a *)(p + 37) = ((unsigned __int128)0xfedcba9876543210ull << 64) | 0x0f1e2d3c4b5a6978ull;
        *(ldbla *)(p + 53) = 1.0L / 3.0L;
        uint64_t sum = *(u16a *)(p + 1) + *(u32a *)(p + 3) + *(u64a *)(p + 7) + (uint64_t)(*(f32a *)(p + 17) * 4) +
            (uint64_t)(int64_t)(*(f64a *)(p + 21) * 4) + (uint64_t)(uintptr_t)*(ptra *)(p + 29) + (uint64_t)(*(u128a *)(p + 37) >> 32) +
            (uint64_t)(*(ldbla *)(p + 53) * 3.0e9L);
        printf("offset %d sum %016llx bytes %016llx\n", offset, (unsigned long long)sum, (unsigned long long)checksum());
    }
    struct record records[3];
    memset(records, 0, sizeof records);
    for (int i = 0; i < 3; i++) { records[i].tag = (char)('a' + i); records[i].wide = 0x1000000000000001ull * (i + 1); records[i].half = (uint16_t)(0x8001 + i); records[i].real = 0.5 * i; records[i].tail = 'z'; records[i].extended = i / 7.0L; }
    unsigned char raw[sizeof records];
    memcpy(raw, records, sizeof records);
    memmove(raw + 3, raw, sizeof raw - 3);     /* overlapping forward move */
    memmove(raw, raw + 5, sizeof raw - 5);     /* overlapping backward move */
    uint64_t h = 7;
    for (size_t i = 0; i < sizeof raw; i++) h = h * 31 + raw[i];
    printf("records %zu fields %llx %u %.2f %c raw %016llx\n", sizeof(struct record), (unsigned long long)records[2].wide, records[1].half, records[2].real, records[0].tail, (unsigned long long)h);
    volatile uint32_t counter = 0;
    volatile unsigned char *mapped = buffer + 3;
    for (int i = 0; i < 1000; i++) { counter = counter + 1; mapped[i % 8] = (unsigned char)i; }
    printf("volatile %u %u\n", counter, mapped[5]);
    return 0;
}
