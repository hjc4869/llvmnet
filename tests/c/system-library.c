#include <stdarg.h>
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