#include <stdio.h>
#include <stdlib.h>

#ifdef __clang__
#pragma clang diagnostic ignored "-Wdeprecated-non-prototype"
#endif

typedef long (*legacy_integer)();
typedef double (*legacy_float)();
typedef void *(*legacy_allocator)();

static long add(long left, long right)
{
    return left + right;
}

static double blend(int label, double fraction, long integral, double bias)
{
    return label + fraction + integral + bias;
}

static legacy_integer volatile integer_callback = add;
static legacy_float volatile float_callback = blend;
static legacy_allocator volatile allocator_callback = malloc;

int main(void)
{
    if (integer_callback(19L, 23L) != 42)
        return 1;
    if (float_callback(3, 1.25, 17L, 0.5) != 21.75)
        return 2;
    unsigned char *memory = allocator_callback((size_t)100);
    if (!memory)
        return 3;
    memory[99] = 42;
    if (memory[99] != 42)
        return 4;
    free(memory);
    puts("legacy fixed callbacks passed");
    return 0;
}