/* musl stdio, conversion and string functions; compared with native musl 1.2.5. */
#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <inttypes.h>
#include <wchar.h>
static int compare(const void *a, const void *b) { int x = *(const int *)a, y = *(const int *)b; return (x > y) - (x < y); }
int main(void)
{
    int n = 0;
    printf("[%d|%5i|%-5u|%+.3d|%x|%#o|%lld|%zu|%jd]\n", -42, 7, 9u, 5, 0xbeef, 8, -9000000000LL, (size_t)77, (intmax_t)-1);
    printf("[%f|%.3e|%g|%G|%a|%A|%.0f|%10.4f]\n", 3.14159, 12345.678, 0.0001, 1e-10, 1.5, -0.375, 2.5, -1.0 / 3);
    printf("[%Lf|%.20Lg|%La|%Le]\n", 1.0L / 3, 2.0L / 3, 1.0L, 1e4000L);
    printf("[%s|%.3s|%8s|%-8s|%c|%%|%2$s %1$s]%n\n", "alpha", "omega", "pad", "left", 'Z', &n);
    printf("written %d [%s %s]\n", n, "x", "y");
    printf("[%2$s %1$s %3$d]\n", "world", "hello", 3);
    printf("[%ls|%lc]\n", L"wide", (wint_t)L'w');
    char *end;
    printf("strtod %.17g %s | strtold %.21Lg | strtol %ld %lu %lld | strtof %a\n", strtod("  -1.5e-3x", &end), end,
        strtold("0.1", 0), strtol("-0x7fz", 0, 0), strtoul("777", 0, 8), strtoll("-9223372036854775808", 0, 10), strtof("1e-40", 0));
    int a = 0; double b = 0; char word[16] = {0}; unsigned x = 0;
    int fields = sscanf("17 2.5e2 token 0x1f", "%d %lf %15s %x", &a, &b, word, &x);
    printf("sscanf %d: %d %g %s %u\n", fields, a, b, word, x);
    int values[] = { 9, -3, 5, 0, 12, -7, 5 };
    qsort(values, 7, sizeof *values, compare);
    int key = 5; int *found = bsearch(&key, values, 7, sizeof *values, compare);
    printf("sorted %d %d %d %d %d %d %d found=%d\n", values[0], values[1], values[2], values[3], values[4], values[5], values[6], found ? (int)(found - values) >= 4 : -1);
    char text[] = "a,b,,c", *save = 0;
    for (char *token = strtok_r(text, ",", &save); token; token = strtok_r(0, ",", &save)) printf("token %s\n", token);
    printf("%zu %d %s %s %p\n", strlen("hello"), strcmp("abc", "abd") < 0, strstr("haystack", "st"), strrchr("a/b/c", '/'), (void *)memchr("xyz", 'q', 3));
    FILE *file = tmpfile();
    for (int i = 0; i < 1000; i++) fprintf(file, "%04d,", i);
    fseek(file, 0, SEEK_END); long size = ftell(file); fseek(file, 4995, SEEK_SET);
    char chunk[11] = {0}; size_t got = fread(chunk, 1, 10, file);
    ungetc('#', file); int next = fgetc(file);
    printf("tmpfile size=%ld fread=%zu [%s] ungetc=%c\n", size, got, chunk, next);
    rewind(file); char *line = 0; size_t capacity = 0; ssize_t length = getdelim(&line, &capacity, ',', file);
    printf("getdelim %zd [%s]\n", length, line);
    free(line); fclose(file);
    char buffer[8]; int needed = snprintf(buffer, sizeof buffer, "%s", "truncate me");
    printf("snprintf needed=%d [%s]\n", needed, buffer);
    char *formatted; asprintf(&formatted, "%s-%d", "dyn", 99); printf("asprintf %s\n", formatted); free(formatted);
    return 0;
}
