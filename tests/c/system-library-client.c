#include <stdio.h>
struct pair { long integer; double real; };
struct large { long fields[5]; };
extern int external_counter;
extern struct pair external_pair(struct pair);
extern struct large external_large(struct large);
extern double external_variadic(int, ...);
extern long external_callback(long (*)(long), long);
static long callback(long value) { return value * 3; }
int main(void)
{
    struct pair small = external_pair((struct pair){14, 1.25});
    if (small.integer != 17 || small.real != 2.5) return 1;
    struct large original = {{3, 4, 5, 6, 7}};
    struct large copied = external_large(original);
    if (copied.fields[4] != 10 || original.fields[4] != 7) return 2;
    if (external_variadic(3, 1.25, 2.5, 3.75) != 7.5) return 3;
    external_counter = 9;
    if (external_callback(callback, 11) != 42) return 4;
    puts("native C library: aggregates, sret/byval, varargs, global data and callback");
    return 0;
}