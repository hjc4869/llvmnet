#include <errno.h>
#include <stdio.h>
#include <sys/stat.h>
#include <time.h>
#include <utime.h>

int main(void)
{
    const char *path = "utime-test.dat";
    FILE *file = fopen(path, "w");
    if (!file) return 1;
    if (fclose(file)) return 2;
    struct utimbuf times = {1670000000, 1680000000};
    if (utime(path, &times)) return 3;
    struct stat metadata;
    if (stat(path, &metadata)) return 4;
    if (metadata.st_atime != times.actime || metadata.st_mtime != times.modtime) return 5;
    time_t before = time(0);
    if (utime(path, 0) || stat(path, &metadata)) return 6;
    time_t after = time(0);
    if (metadata.st_atime < before || metadata.st_atime > after) return 7;
    if (metadata.st_mtime < before || metadata.st_mtime > after) return 8;
    if (remove(path)) return 9;
    errno = 0;
    if (utime(path, &times) != -1 || errno != ENOENT) return 10;
    return 0;
}