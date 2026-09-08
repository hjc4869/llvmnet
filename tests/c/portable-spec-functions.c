#include <string.h>
#include <math.h>
#include <errno.h>
#include <stdio.h>
#include <unistd.h>
#include <arpa/inet.h>
#include <time.h>
#include <stdint.h>
#include <stdlib.h>
#include <sys/stat.h>

int main(void)
{
    char text[16];
    memset(text, '!', sizeof(text));
    strcpy(text, "ab");
    if (strncat(text, "cdef", 2) != text || strcmp(text, "abcd")) return 1;
    if (text[5] != '!') return 2;
    if (strncat(text, "xy", 0) != text || strcmp(text, "abcd")) return 3;
    if (strncat(text, "e", 5) != text || strcmp(text, "abcde")) return 4;
    if (text[6] != '!') return 5;
    if (strncat(text, "", 3) != text || strcmp(text, "abcde")) return 6;
    if (strcasecmp("aBc", "AbC") || strncasecmp("ABx", "abY", 2)) return 7;
    if (strncasecmp("z", "a", 0) || strncasecmp("aa", "a", 2) <= 0) return 8;
    if (fabs(atanh(0.5) - 0.5493061443340548) > 1e-14) return 9;
    if (fabsf(atanhf(-0.5f) + 0.5493061443340548f) > 1e-6f) return 10;
    if (!signbit(atanh(-0.0)) || !signbit(atanhf(-0.0f))) return 11;
    errno = 0;
    if (!isnan(atanh(2.0)) || errno != EDOM) return 12;
    errno = 0;
    if (atanhf(-1.0f) != -INFINITY || errno != ERANGE) return 13;
    FILE *file = fopen("truncate.fixture", "w+b");
    if (!file) return 14;
    int descriptor = fileno(file);
    if (write(descriptor, "abcdef", 6) != 6) return 15;
    if (ftruncate(descriptor, 2) || lseek(descriptor, 0, SEEK_CUR) != 6) return 16;
    if (ftruncate(descriptor, 8) || lseek(descriptor, 0, SEEK_CUR) != 6) return 17;
    if (lseek(descriptor, 0, SEEK_SET) != 0 || read(descriptor, text, 8) != 8) return 18;
    if (memcmp(text, "ab\0\0\0\0\0\0", 8)) return 19;
    errno = 0;
    if (ftruncate(descriptor, -1) != -1 || errno != EINVAL) return 20;
    if (ftruncate(descriptor, 0) || lseek(descriptor, 0, SEEK_CUR) != 8) return 21;
    if (fclose(file)) return 22;
    errno = 0;
    if (ftruncate(descriptor, 0) != -1 || errno != EBADF) return 23;
    if (remove("truncate.fixture")) return 24;
    uint16_t (*swap16)(uint16_t) = htons;
    uint32_t (*swap32)(uint32_t) = htonl;
    volatile uint16_t short_value = 0x1234;
    volatile uint32_t long_value = 0x12345678;
    if (swap16(short_value) != 0x3412 || swap32(long_value) != 0x78563412) return 25;
    if (ntohs(htons(0xabcd)) != 0xabcd || ntohl(htonl(0xfedcba98)) != 0xfedcba98) return 26;
    if (htons(0) || htonl(0) || ntohs(0xffff) != 0xffff || ntohl(0xffffffff) != 0xffffffff) return 27;
    if (fabs(tgamma(5) - 24) > 1e-12 || fabs(tgamma(0.5) - sqrt(M_PI)) > 1e-12) return 28;
    if (fabs(tgamma(-0.5) + 2 * sqrt(M_PI)) > 1e-12 || fabsf(tgammaf(5) - 24) > 1e-5f) return 29;
    errno = 0;
    if (tgamma(-0.0) != -INFINITY || errno != ERANGE) return 30;
    errno = 0;
    if (!isnan(tgamma(-1)) || errno != EDOM) return 31;
    errno = 0;
    if (tgamma(172) != INFINITY || errno != ERANGE) return 32;
    errno = 0;
    if (tgammaf(36) != INFINITY || errno != ERANGE) return 33;
    errno = 0;
    if (!isnan(tgamma(-INFINITY)) || errno != EDOM) return 34;
    errno = 0;
    if (!isnan(tgamma(NAN)) || tgamma(INFINITY) != INFINITY || errno) return 35;
    if (difftime(123, 100) != 23 || difftime(100, 123) != -23) return 36;
    if (difftime(INT64_MAX, INT64_MAX - 1) != 1) return 37;
    if (difftime(INT64_MIN, INT64_MAX) != -18446744073709551616.0) return 38;
    char original[4096], current[4096];
    if (getcwd(original, sizeof(original)) != original) return 39;
    errno = 0;
    if (getcwd(text, 1) || errno != ERANGE) return 40;
    errno = 0;
    if (getcwd(text, 0) || errno != EINVAL) return 41;
    char *allocated = getcwd(NULL, 0);
    if (!allocated || strcmp(allocated, original)) return 42;
    free(allocated);
    if (mkdir("cwd.fixture", 0700) || chdir("cwd.fixture")) return 43;
    if (!getcwd(current, sizeof(current)) || strcmp(current, original) == 0) return 44;
    if (chdir(original) || rmdir("cwd.fixture")) return 45;
    errno = 0;
    if (chdir("cwd.fixture") != -1 || errno != ENOENT) return 46;
    return 0;
}