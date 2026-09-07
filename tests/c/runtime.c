#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <stdint.h>

__attribute__((noinline)) static long sum(int count, ...)
{
    va_list arguments;
    va_start(arguments, count);
    long result = 0;
    for (int index = 0; index < count; index++)
        result += va_arg(arguments, long);
    va_end(arguments);
    return result;
}

__attribute__((noinline)) static double mixed(int count, double initial, ...)
{
    va_list arguments, copy;
    va_start(arguments, initial);
    va_copy(copy, arguments);
    double result = initial;
    for (int index = 0; index < count; index++) {
        result += va_arg(arguments, double);
        result += va_arg(arguments, int);
    }
    if (va_arg(copy, double) != 0.5)
        result = -100;
    va_end(copy);
    va_end(arguments);
    return result;
}

int main(int argc, char **argv)
{
    if (argc != 2 || strcmp(argv[1], "payload") || argv[argc] != NULL) return 1;
    if (sum(9, 1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L) != 45) return 2;
    if (mixed(9, 0.25, 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5, 5.5, 6, 6.5, 7, 7.5, 8, 8.5, 9) != 85.75) return 3;
    char *memory = calloc(32, 1);
    if (!memory || memory[31]) return 4;
    strcpy(memory, "abcdef");
    memmove(memory + 1, memory, 6);
    if (strcmp(memory, "aabcdef")) return 5;
    memory = realloc(memory, 128);
    if (!memory || strlen(memory) != 7) return 6;
    char *copy = strdup(memory);
    if (!copy || memcmp(copy, memory, 8)) return 7;
    free(copy);
    free(memory);
    char buffer[128];
    int count = -1;
    int length = snprintf(buffer, sizeof(buffer), "%+06d %#llx %.*f %-5.3s%n", -42, 0xabcdef1234ULL, 2, 3.125, "hello", &count);
    if (length != count || strcmp(buffer, "-00042 0xabcdef1234 3.12 hel  ")) return 8;
    char small[4];
    if (snprintf(small, sizeof(small), "%s", "abcdef") != 6 || strcmp(small, "abc")) return 9;
    if (snprintf(NULL, 0, "%d", 12345) != 5) return 10;
    if (setenv("LLVMNET_RUNTIME_TEST_VALUE", "managed", 1)) return 11;
    if (strcmp(getenv("LLVMNET_RUNTIME_TEST_VALUE"), "managed")) return 12;
    if (unsetenv("LLVMNET_RUNTIME_TEST_VALUE") || getenv("LLVMNET_RUNTIME_TEST_VALUE") != NULL) return 13;
    printf("runtime: %s, sum=%ld, mixed=%.2f\n", buffer, sum(3, 10L, 20L, 30L), mixed(1, 0.25, 0.5, 1));
    return 0;
}