// stdio buffer sizes (PF-03): tests/perf/fwrite.c with the stream's buffer given to setvbuf: 1 GiB written to the
// file named by argv[1] in 256-byte fwrites, then read back. argv[2] is the buffer size, 0 for the C library's default
// (musl ignores a size passed without a buffer, so the buffer is allocated here).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(int count, char **arguments)
{
    if (count != 3) return 2;
    size_t size = (size_t)atol(arguments[2]);
    static char block[256], buffer[65536];
    for (int index = 0; index < (int)sizeof block; index++) block[index] = (char)index;
    FILE *file = fopen(arguments[1], "wb");
    if (!file) return 3;
    if (size) setvbuf(file, malloc(size), _IOFBF, size);
    for (long written = 0; written < 1L << 30; written += sizeof block)
        if (fwrite(block, 1, sizeof block, file) != sizeof block) return 4;
    if (fclose(file) != 0) return 5;
    file = fopen(arguments[1], "rb");
    if (!file) return 6;
    unsigned long sum = 0;
    size_t length;
    while ((length = fread(buffer, 1, sizeof buffer, file)) > 0)
        for (size_t index = 0; index < length; index += 4096) sum += (unsigned char)buffer[index];
    fclose(file);
    remove(arguments[1]);
    printf("%lu\n", sum);
    return 0;
}
