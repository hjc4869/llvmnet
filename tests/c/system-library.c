#include <stdarg.h>
#include <stdint.h>
struct pair { long integer; double real; };
struct large { long fields[5]; };
int external_counter = 7;
struct pair external_pair(struct pair value)
{
    value.integer += 3;
    value.real *= 2;
    return value;
}
struct large external_large(struct large value)
{
    value.fields[4] += value.fields[0];
    return value;
}
double external_variadic(int count, ...)
{
    va_list arguments;
    va_start(arguments, count);
    double total = 0;
    for (int index = 0; index < count; index++) total += va_arg(arguments, double);
    va_end(arguments);
    return total;
}
long external_callback(long (*callback)(long), long value) { return callback(value) + external_counter; }
/* Narrow integers across the boundary through function pointers: clang's callee relies on the caller extending narrow
 * arguments (signext, zeroext) and leaves the upper bits of a narrow result undefined. */
static int32_t external_narrow(uint8_t byte, int16_t half, int8_t small) { return byte + half + small; }
static uint8_t external_low(uint32_t value) { return (uint8_t)value; }
int32_t (*external_narrow_pointer(void))(uint8_t, int16_t, int8_t) { return external_narrow; }
uint8_t (*external_low_pointer(void))(uint32_t) { return external_low; }
int32_t external_narrow_callback(int32_t (*callback)(int16_t, uint8_t, int8_t)) { return callback(-1, 255, -2); }
