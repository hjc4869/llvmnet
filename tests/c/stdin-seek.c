// Standard input redirected from a file is that file, as natively: fstat, lseek and rewind(stdin) work on it, and
// read moves the descriptor's offset (lseek reports it). From a pipe, lseek fails with ESPIPE.
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

int main(void)
{
    struct stat status;
    if (fstat(0, &status) != 0) return 1;
    printf("regular %d size %lld\n", S_ISREG(status.st_mode) ? 1 : 0, S_ISREG(status.st_mode) ? (long long)status.st_size : -1);
    char buffer[16] = {0};
    if (read(0, buffer, 5) != 5) return 2;
    printf("read '%s'\n", buffer);
    off_t position = lseek(0, 0, SEEK_CUR);
    printf("position %lld%s\n", (long long)position, position < 0 && errno == ESPIPE ? " ESPIPE" : "");
    if (position < 0) return 0;
    if (lseek(0, 12, SEEK_SET) != 12) return 3;
    memset(buffer, 0, sizeof buffer);
    if (read(0, buffer, 4) != 4) return 4;
    printf("at 12 '%s'\n", buffer);
    printf("from end %lld\n", (long long)lseek(0, -3, SEEK_END));
    char line[64];
    rewind(stdin);
    if (!fgets(line, sizeof line, stdin)) return 5;
    printf("first line again: %s", line);
    if (!fgets(line, sizeof line, stdin)) return 6;
    printf("second line: %s", line);
    return 0;
}
