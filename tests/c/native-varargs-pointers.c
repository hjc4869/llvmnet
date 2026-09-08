#define _GNU_SOURCE
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

static int (*volatile format)(char *, size_t, const char *, ...) = snprintf;
static int (*volatile open_file)(const char *, int, ...) = open;
static int (*volatile control)(int, int, ...) = fcntl;
static int (*volatile control64)(int, int, ...) = fcntl64;

int main(int count, char **arguments)
{
    if (count != 2)
        return 1;
    char buffer[256];
    int length = format(buffer, sizeof buffer,
        "%d %ld %.2f %d %ld %.2f %d %ld %.2f %d %ld %.2f",
        1, 2L, 3.25, 4, 5L, 6.5, 7, 8L, 9.75, 10, 11L, 12.5);
    if (length != (int)strlen("1 2 3.25 4 5 6.50 7 8 9.75 10 11 12.50") || strcmp(buffer, "1 2 3.25 4 5 6.50 7 8 9.75 10 11 12.50"))
        return 2;
    const char *expected = "1.25 2.25 3.25 4.25 5.25 6.25 7.25 8.25 9.25 10.25 11.25 12.25";
    length = format(buffer, sizeof buffer, "%.2f %.2f %.2f %.2f %.2f %.2f %.2f %.2f %.2f %.2f %.2f %.2Lf",
        1.25, 2.25, 3.25, 4.25, 5.25, 6.25, 7.25, 8.25, 9.25, 10.25, 11.25, 12.25L);
    if (length != (int)strlen(expected) || strcmp(buffer, expected))
        return 8;
    int descriptor = open_file(arguments[1], O_RDWR | O_CREAT | O_TRUNC, 0600);
    if (descriptor < 0)
        return 3;
    if (control(descriptor, F_SETFD, FD_CLOEXEC) != 0 || control64(descriptor, F_GETFD) != FD_CLOEXEC)
        return 4;
    struct flock lock = {0};
    lock.l_type = F_WRLCK;
    lock.l_whence = SEEK_SET;
    if (control64(descriptor, F_SETLK, &lock) != 0)
        return 5;
    lock.l_type = F_UNLCK;
    if (control(descriptor, F_SETLK, &lock) != 0 || close(descriptor) != 0)
        return 6;
    if (unlink(arguments[1]) != 0)
        return 7;
    puts("native variadic function pointers passed");
    return 0;
}