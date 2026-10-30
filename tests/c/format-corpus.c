/* printf, scanf and strtod corpus: every conversion of the formatted I/O functions over edge-case
 * values, including %a, %Lf and %La, %n, positional arguments, flags and wide strings, plus strtod/strtold and
 * sscanf round trips. The portable build must print exactly what the same program prints against native musl. */
#include <float.h>
#include <locale.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

static const double doubles[] = {0.0, -0.0, 1.0, -1.5, 0.1, 1e-310, 5e-324, DBL_MAX, DBL_MIN, 123456789.125, 1e21, 1e-7,
    2.5, 3.5, 0.5, 1.0 / 3, INFINITY, -INFINITY, NAN};
static const long double longs[] = {0.0L, 1.0L, -1.5L, 0.1L, 1e-4950L, LDBL_MAX, LDBL_MIN, 1.0L / 3, 12345678901234567890.0L};
static const char *const double_formats[] = {"%f", "%.0f", "%.20f", "%e", "%.3e", "%E", "%g", "%.17g", "%G", "%a", "%.3a", "%A",
    "%+f", "% f", "%#.0f", "%#g", "%015.4f", "%-12.3e|", "%'.2f"};
static const char *const long_formats[] = {"%Lf", "%.25Lf", "%Le", "%Lg", "%.21Lg", "%La", "%.5La"};
static const long long integers[] = {0, 1, -1, 42, 127, -128, 32767, INT32_MAX, INT32_MIN, INT64_MAX, INT64_MIN, 1234567};
static const char *const integer_formats[] = {"%lld", "%+lld", "% lld", "%010lld", "%-10lld|", "%llx", "%#llx", "%llX", "%llo",
    "%#llo", "%llu", "%.5lld", "%'lld", "%hhd", "%hd"};
static const char *const numbers[] = {"0", "-0", "1e400", "-1e400", "4.9e-324", "2.4e-324", "0x1.fffffffffffffp1023", "0x1p-1074",
    "inf", "-Infinity", "nan", "nan(123)", "1.7976931348623157e308", "123.456e-2", "  42abc", "0x1.8p1", ".5", "5.", "1e", "+.e1"};

int main(void)
{
    char text[512];
    for (size_t f = 0; f < sizeof double_formats / sizeof *double_formats; f++)
        for (size_t v = 0; v < sizeof doubles / sizeof *doubles; v++) {
            snprintf(text, sizeof text, double_formats[f], doubles[v]);
            printf("%s %zu: %s\n", double_formats[f], v, text);
        }
    for (size_t f = 0; f < sizeof long_formats / sizeof *long_formats; f++)
        for (size_t v = 0; v < sizeof longs / sizeof *longs; v++) {
            snprintf(text, sizeof text, long_formats[f], longs[v]);
            printf("%s %zu: %s\n", long_formats[f], v, text);
        }
    for (size_t f = 0; f < sizeof integer_formats / sizeof *integer_formats; f++)
        for (size_t v = 0; v < sizeof integers / sizeof *integers; v++) {
            if (strstr(integer_formats[f], "hh")) snprintf(text, sizeof text, integer_formats[f], (signed char)integers[v]);
            else if (strstr(integer_formats[f], "h")) snprintf(text, sizeof text, integer_formats[f], (short)integers[v]);
            else snprintf(text, sizeof text, integer_formats[f], integers[v]);
            printf("%s %zu: %s\n", integer_formats[f], v, text);
        }
    int written = 0, total = snprintf(text, sizeof text, "%s%n|%c|%%|%p", "ab", &written, 'z', (void *)0);
    printf("%%n: %d %d [%s]\n", total, written, text);
    /* A format uses positional arguments for all conversions or for none. */
    total = snprintf(text, sizeof text, "%2$s %1$s|%3$*4$.*5$f|%1$s", "first", "second", 3.14159, 10, 2);
    printf("positional: %d [%s]\n", total, text);
    printf("strings: [%5s] [%-5s] [%.2s] [%ls] [%lc]\n", "ab", "ab", "abcdef", L"wide é", (wint_t)L'ü');
    for (size_t index = 0; index < sizeof numbers / sizeof *numbers; index++) {
        char *end;
        double value = strtod(numbers[index], &end);
        long double extended = strtold(numbers[index], 0);
        float single = strtof(numbers[index], 0);
        printf("strtod(%s) = %a %La %a, rest [%s]\n", numbers[index], value, extended, single, end);
    }
    int i = 0, n = 0; double d = 0; char word[16] = ""; unsigned u = 0; long double l = 0;
    int matched = sscanf("  -42 3.25e2 hello 0x1F 1.5", "%d %lf %15s %x %Lf%n", &i, &d, word, &u, &l, &n);
    printf("sscanf: %d %d %a %s %u %La %d\n", matched, i, d, word, u, l, n);
    matched = sscanf("12abc", "%d%3[a-c]", &i, word);
    printf("sscanf scanset: %d %d %s\n", matched, i, word);
    wchar_t wide[64];
    swprintf(wide, 64, L"%d %ls %.2f", 7, L"wörd", 2.5);
    printf("swprintf: %ls\n", wide);
    printf("setlocale C.UTF-8: %s\n", setlocale(LC_ALL, "C.UTF-8") ? "yes" : "no");
    return 0;
}
