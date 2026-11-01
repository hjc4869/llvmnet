/* RL-02: compiled once into compat-object-v2.o (LLVM bitcode tagged dotnet64-v2) and committed. tests/abi-freeze.sh
 * links the committed object with a freshly compiled compat-main.c, so a toolchain whose ABI drifted from the frozen
 * dotnet64-v2 (structure layouts, varargs, long double, errno, jmp_buf, callbacks, stdio) fails to link or prints
 * something else. Rebuild the object only together with a new ABI tag. */
#include <errno.h>
#include <setjmp.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>

struct pair { char tag; long long value; double weight; };

struct pair compat_make(char tag, long long value, double weight)
{
    struct pair result = { tag, value, weight };
    return result;
}

long double compat_scale(struct pair pair, long double factor)
{
    return (long double)pair.value * factor + pair.weight;
}

int compat_format(char *buffer, size_t size, const char *format, ...)
{
    va_list arguments;
    va_start(arguments, format);
    int length = vsnprintf(buffer, size, format, arguments);
    va_end(arguments);
    return length;
}

int compat_missing(const char *path)
{
    struct stat information;
    errno = 0;
    return stat(path, &information) == 0 ? 0 : errno;
}

void compat_jump(jmp_buf target, int value)
{
    longjmp(target, value);
}

static int compare(const void *left, const void *right)
{
    return *(const int *)left - *(const int *)right;
}

void compat_sort(int *values, size_t count, int (*order)(const void *, const void *))
{
    qsort(values, count, sizeof *values, order ? order : compare);
}

void compat_print(FILE *stream, const struct tm *time)
{
    char text[64];
    strftime(text, sizeof text, "%Y-%m-%d %H:%M:%S", time);
    fprintf(stream, "time %s yday %d\n", text, time->tm_yday);
}
