#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/stat.h>
#include <errno.h>
extern long __isoc23_strtol(const char *, char **, int);

static int compare(const void *left, const void *right)
{
    int first = *(const int *)left;
    int second = *(const int *)right;
    return (first > second) - (first < second);
}

int main(int argc, char **argv)
{
    if (argc != 2) return 1;
    FILE *file = fopen(argv[1], "w+b");
    if (!file) return 2;
    setbuf(file, NULL);
    unsigned char original[] = {0, 255, 7, 9, 128};
    if (fwrite(original, 1, sizeof(original), file) != sizeof(original)) return 3;
    if (fflush(file) || ftell(file) != 5 || fseek(file, 1, SEEK_SET)) return 4;
    unsigned char buffer[8] = {0};
    if (fread(buffer, 1, 8, file) != 4 || !feof(file) || ferror(file)) return 5;
    if (memcmp(buffer, original + 1, 4)) return 6;
    rewind(file);
    if (feof(file) || fgetc(file) != 0 || fgetc(file) != 255) return 7;
    if (fclose(file)) return 8;
    int descriptor = open(argv[1], O_RDONLY);
    if (descriptor < 0) return 9;
    struct stat metadata;
    if (fstat(descriptor, &metadata) || metadata.st_size != 5 || !S_ISREG(metadata.st_mode)) return 10;
    if (lseek(descriptor, -2, SEEK_END) != 3 || read(descriptor, buffer, 2) != 2 || buffer[0] != 9 || buffer[1] != 128) return 11;
    int duplicate = dup(descriptor);
    if (duplicate < 0 || lseek(duplicate, 0, SEEK_SET) || read(descriptor, buffer, 1) != 1 || buffer[0] != 0) return 22;
    if (close(descriptor) || read(duplicate, buffer, 1) != 1 || buffer[0] != 255) return 23;
    if (close(duplicate)) return 12;
    file = fopen(argv[1], "w+");
    if (!file || fputs("line1\nlast", file) < 0) return 24;
    rewind(file);
    if (!fgets((char *)buffer, sizeof(buffer), file) || strcmp((char *)buffer, "line1\n")) return 25;
    if (!fgets((char *)buffer, sizeof(buffer), file) || strcmp((char *)buffer, "last")) return 26;
    if (fgets((char *)buffer, sizeof(buffer), file) || !feof(file)) return 27;
    if (fclose(file)) return 28;
    file = fopen(argv[1], "w+");
    if (!file || fputs("17 2.5 tail\n31\n", file) < 0) return 30;
    rewind(file);
    int scanned;
    double scanned_real;
    if (fscanf(file, "%d %lf", &scanned, &scanned_real) != 2 || scanned != 17 || scanned_real != 2.5) return 31;
    if (fgetc(file) != ' ' || fscanf(file, "%*s %d", &scanned) != 1 || scanned != 31 || fgetc(file) != '\n') return 32;
    if (fclose(file) || unlink(argv[1])) return 33;
    errno = 0;
    if (open(argv[1], O_RDONLY) != -1 || errno != ENOENT) return 13;
    char *end;
    if (strtol(" -0x1f rest", &end, 0) != -31 || strcmp(end, " rest")) return 14;
    if (strtoull("18446744073709551615", &end, 10) != UINT64_MAX || *end) return 15;
    errno = 0;
    if (strtoll("9223372036854775808", &end, 10) != INT64_MAX || errno != ERANGE) return 16;
    if (strtod(" -12.5e2end", &end) != -1250 || strcmp(end, "end")) return 17;
    if (__isoc23_strtol("-0b101101rest", &end, 0) != -45 || strcmp(end, "rest")) return 29;
    int integer, consumed = 0;
    double real;
    char word[8];
    if (sscanf("-12:3.5 abc9", "%d:%lf %3[a-z]%n", &integer, &real, word, &consumed) != 3) return 18;
    if (integer != -12 || real != 3.5 || strcmp(word, "abc") || consumed != 11) return 19;
    int array[] = {7, -4, 0, 19, 2};
    qsort(array, 5, sizeof(int), compare);
    for (int index = 1; index < 5; index++) if (array[index - 1] > array[index]) return 20;
    int key = 7;
    if (*(int *)bsearch(&key, array, 5, sizeof(int), compare) != 7) return 21;
    fprintf(stdout, "io: %d %.1f %s %d\n", integer, real, word, consumed);
    return 0;
}