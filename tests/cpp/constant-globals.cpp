// Global and constant addresses read from code that runs before main and from hot loops (JP-04). The backend marks
// the address fields on Program initonly, and RyuJIT reads those as constants in code it optimizes after Program's
// type initializer has run; fields in the holder classes past Program stay writable. The constructor and the static
// object's constructor have loops, so under DOTNET_TC_QuickJitForLoops=0 or DOTNET_TieredCompilation=0 they are
// optimized at their first call, before main, while main's loops reach optimized code through on-stack replacement or
// tier 1. many_table points at MANY globals defined in another source, enough that the last ones live in a holder
// class. In system mode, stdout and optind are glibc's, resolved by symbol in the type initializer. Every result is
// an integer, so the output equals the native build's.
#include <cstdio>
#include <cstring>
#include <getopt.h>

#ifndef MANY
#define MANY 1
#endif
extern int *const many_table[MANY];

static unsigned samples[512];
static const int weights[] = {3, 1, 4, 1, 5, 9, 2, 6};
int histogram[64];
const char *const names[] = {"alpha", "beta", "gamma"};
static int *const cursor = &histogram[5];
static long seen_by_constructor;
static FILE *stream_at_start;

static long sum_table(int stride) {
    long total = 0;
    for (int index = 0; index < MANY; index += stride)
        total += *many_table[index] * (index % 7 + 1);
    return total + *many_table[MANY - 1];
}

__attribute__((constructor)) static void early() {
    for (int index = 0; index < 512; index++)
        samples[index] = index * 3u + weights[index % 8];
    for (int index = 0; index < 512; index++)
        histogram[samples[index] & 63]++;
    *cursor += 1000;
    seen_by_constructor = sum_table(97) + histogram[5] + static_cast<long>(std::strlen(names[2])) + optind;
    stream_at_start = stdout;
}

// Independent of early(), which may run before or after it.
struct Object {
    long value = 0;
    Object() {
        for (int index = 0; index < 64; index++)
            value += weights[index % 8] * (index + 1);
        value += sum_table(101) + static_cast<long>(std::strlen(names[0])) * optind;
    }
};
static Object object;

int main() {
    long total = 0;
    for (int round = 0; round < 3000; round++)
        for (int index = 0; index < 512; index++) {
            samples[index] = samples[index] * 5 + weights[(index + round) % 8];
            histogram[index & 63] += samples[index] & 1;
        }
    for (int call = 0; call < 200; call++)
        total += sum_table(1 + call % 13);
    unsigned long checksum = 0;
    for (int index = 0; index < 64; index++)
        checksum = checksum * 31 + histogram[index];
    std::printf("constructor %ld\nobject %ld\ntables %ld\nhistogram %lu\nsamples %u %u\ncursor %d\nstream %s\n",
                seen_by_constructor, object.value, total, checksum, samples[0], samples[511], *cursor,
                stream_at_start == stdout ? "same" : "different");
    return 0;
}
