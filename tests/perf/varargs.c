// Variadic calls (PF-01): 40 million calls to a function that reads six integer and floating-point arguments with
// va_arg, and 4 million vsnprintf calls through a variadic wrapper. Prints a checksum.
#include <stdarg.h>
#include <stdio.h>

static __attribute__((noinline)) double sum(int count, ...)
{
    va_list arguments;
    va_start(arguments, count);
    double total = 0;
    for (int index = 0; index < count; index++)
        total += index & 1 ? va_arg(arguments, double) : (double)va_arg(arguments, long);
    va_end(arguments);
    return total;
}

static __attribute__((noinline)) int format(char *buffer, size_t size, const char *pattern, ...)
{
    va_list arguments;
    va_start(arguments, pattern);
    int length = vsnprintf(buffer, size, pattern, arguments);
    va_end(arguments);
    return length;
}

int main(void)
{
    double total = 0;
    for (long index = 0; index < 40000000; index++)
        total += sum(6, index, index * 0.5, index & 255, 1.25, -index, 3.0);
    char buffer[64];
    long length = 0;
    for (int index = 0; index < 4000000; index++)
        length += format(buffer, sizeof buffer, "%d:%x:%s", index, index * 7, index & 1 ? "odd" : "even");
    printf("%.1f %ld %s\n", total, length, buffer);
    return 0;
}
