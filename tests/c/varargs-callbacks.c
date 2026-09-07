#include <stdarg.h>
#include <stdio.h>
#include <string.h>

static long sum(int count, ...)
{
    va_list arguments;
    va_start(arguments, count);
    long result = 0;
    for (int index = 0; index < count; index++) {
        int number = va_arg(arguments, int);
        double factor = va_arg(arguments, double);
        result += number * (long)factor;
    }
    va_end(arguments);
    return result;
}

static long other(int count, ...)
{
    return count + 100;
}

static int format(char *buffer, const char *pattern, ...)
{
    va_list arguments;
    va_start(arguments, pattern);
    int result = vsnprintf(buffer, 128, pattern, arguments);
    va_end(arguments);
    return result;
}

static long (*volatile callbacks[])(int, ...) = {sum, other};
static int (*volatile formatter)(char *, const char *, ...) = format;

#ifdef TEST_NATIVE_CALLBACK
extern long invoke_variadic(long (*callback)(int, ...));
#endif

int main(void)
{
#ifdef TEST_NATIVE_CALLBACK
    return (int)invoke_variadic(callbacks[0]);
#else
    if (callbacks[0] != sum || callbacks[1] != other || callbacks[0] == callbacks[1])
        return 1;
    long result = callbacks[0](10, 1, 1.0, 2, 2.0, 3, 3.0, 4, 4.0, 5, 5.0,
                              6, 6.0, 7, 7.0, 8, 8.0, 9, 9.0, 10, 10.0);
    char buffer[128];
    formatter(buffer, "%d %.1f %s", 17, 2.5, "ok");
    printf("callbacks=%ld other=%ld formatted=%s\n", result, callbacks[1](0), buffer);
    return result != 385 || callbacks[1](0) != 100 || strcmp(buffer, "17 2.5 ok");
#endif
}