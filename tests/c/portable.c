#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <time.h>
#include <errno.h>
#include <assert.h>
#include <signal.h>

#if !defined(__LLVMNET_PORTABLE__) || __LLVMNET_ABI_VERSION != 1
#error expected llvmnet portable ABI v1
#endif
#if defined(__linux__) || defined(_WIN32) || defined(__APPLE__) || defined(__x86_64__)
#error portable code must not inherit host OS or CPU selection macros
#endif
_Static_assert(sizeof(long) == 8, "LP64 long");
_Static_assert(sizeof(void *) == 8, "64-bit address space");
_Static_assert(sizeof(wchar_t) == 4, "32-bit wchar_t");
_Static_assert(sizeof(long double) == 16, "portable extended long double layout");
_Static_assert(offsetof(struct tm, tm_gmtoff) == 40, "stable tm layout");

static long sum(int count, ...)
{
    va_list values;
    va_start(values, count);
    long total = 0;
    for (int index = 0; index < count; index++) total += va_arg(values, long);
    va_end(values);
    return total;
}
static volatile sig_atomic_t signal_value;
static void receive_signal(int value) { signal_value = value; }
int main(void)
{
    void *aligned = NULL;
    if (posix_memalign(&aligned, 256, 1024) || ((uintptr_t)aligned & 255)) return 1;
    memset(aligned, 42, 1024);
    aligned = realloc(aligned, 2048);
    if (!aligned || ((unsigned char *)aligned)[1023] != 42) return 2;
    free(aligned);
    aligned = aligned_alloc(64, 128);
    if (!aligned || ((uintptr_t)aligned & 63)) return 3;
    free(aligned);
    errno = ERANGE;
    if (strcmp(strerror(ENOENT), "No such file or directory")) return 6;
    if (strcmp(strerror(ERANGE), "Numerical result out of range")) return 7;
    if (errno != 34 || sum(8, 1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L) != 36) return 4;
    assert(sizeof(size_t) == 8);
    if (signal(SIGUSR1, receive_signal) == SIG_ERR || raise(SIGUSR1) || signal_value != SIGUSR1) return 5;
    fprintf(stdout, "portable ABI 1: LP64, wchar32, extended long double, aligned heap and varargs\n");
    return 0;
}