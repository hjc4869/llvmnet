#include <stdint.h>
#include <stdio.h>
struct pair { long integer; double real; };
struct large { long fields[5]; };
extern int external_counter;
extern struct pair external_pair(struct pair);
extern struct large external_large(struct large);
extern double external_variadic(int, ...);
extern long external_callback(long (*)(long), long);
extern int32_t (*external_narrow_pointer(void))(uint8_t, int16_t, int8_t);
extern uint8_t (*external_low_pointer(void))(uint32_t);
extern int32_t external_narrow_callback(int32_t (*)(int16_t, uint8_t, int8_t));
static long callback(long value) { return value * 3; }
static int32_t narrow_callback(int16_t half, uint8_t byte, int8_t small)
{
    return half == -1 && (uint16_t)half == 65535 && byte == 255 && small == -2 ? 42 : 0;
}
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
    if (external_narrow_pointer()(255, -1, -2) != 252) return 5;
    if (external_low_pointer()(0x1ff) != 255) return 6;
    if (external_narrow_callback(narrow_callback) != 42) return 7;
    puts("native C library: aggregates, sret/byval, varargs, global data, callbacks and narrow integers through pointers");
    return 0;
}