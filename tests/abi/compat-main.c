/* RL-02: the half of the compatibility check that tests/abi-freeze.sh compiles with the current toolchain; the other
 * half is the committed compat-object-v2.o. The output is compared with compat.expected. */
#include <errno.h>
#include <setjmp.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

struct pair { char tag; long long value; double weight; };
struct pair compat_make(char tag, long long value, double weight);
long double compat_scale(struct pair pair, long double factor);
int compat_format(char *buffer, size_t size, const char *format, ...);
int compat_missing(const char *path);
void compat_jump(jmp_buf target, int value);
void compat_sort(int *values, size_t count, int (*order)(const void *, const void *));
void compat_print(FILE *stream, const struct tm *time);

static int descending(const void *left, const void *right) { return *(const int *)right - *(const int *)left; }

int main(void)
{
    struct pair pair = compat_make('q', 1234567890123LL, 0.25);
    printf("pair %c %lld %.2f\n", pair.tag, pair.value, pair.weight);
    printf("scaled %.6Lf\n", compat_scale(pair, 1.5L));
    char buffer[128];
    int length = compat_format(buffer, sizeof buffer, "%d|%s|%.3Lf|%#x|%zu", -42, "text", 2.0L / 3, 255, sizeof(struct pair));
    printf("format %d %s\n", length, buffer);
    printf("missing %d %s\n", compat_missing("/nonexistent/compat"), compat_missing("/nonexistent/compat") == ENOENT ? "ENOENT" : "other");
    jmp_buf target;
    volatile int reached = 0;
    int value = setjmp(target);
    if (value == 0) { reached = 1; compat_jump(target, 7); }
    printf("jump %d %d\n", value, reached);
    int values[] = { 5, 3, 9, 1, 7 };
    compat_sort(values, 5, 0);
    printf("sorted %d %d %d %d %d\n", values[0], values[1], values[2], values[3], values[4]);
    compat_sort(values, 5, descending);
    printf("reversed %d %d %d %d %d\n", values[0], values[1], values[2], values[3], values[4]);
    struct tm time = { .tm_year = 126, .tm_mon = 10, .tm_mday = 1, .tm_hour = 12, .tm_min = 34, .tm_sec = 56, .tm_yday = 304 };
    compat_print(stdout, &time);
    return 0;
}
