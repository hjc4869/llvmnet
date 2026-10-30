// Formatted output (scripts/bench-runtime.sh): 5 million lines of integers, strings, floating point and hexadecimal
// through printf; run with stdout redirected to /dev/null. The checksum of the lengths goes to stderr.
#include <stdio.h>

int main(void)
{
    static const char *names[] = {"alpha", "beta", "gamma", "delta"};
    long total = 0;
    for (int index = 0; index < 5000000; index++)
        total += printf("%d %s %.3f %08x %ld\n", index, names[index & 3], index * 0.125, (unsigned)index * 2654435761u, total);
    fprintf(stderr, "%ld\n", total);
    return 0;
}
