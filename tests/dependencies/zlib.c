#include <stdio.h>
#include <string.h>
#include <zlib.h>
int main(void)
{
    unsigned char input[8192];
    for (int index = 0; index < (int)sizeof(input); index++)
        input[index] = (unsigned char)((index * 13) ^ (index >> 3));
    unsigned char compressed[16384];
    unsigned char output[8192];
    uLongf compressed_size = sizeof(compressed);
    if (compress2(compressed, &compressed_size, input, sizeof(input), 6) != Z_OK) return 1;
    uLongf output_size = sizeof(output);
    if (uncompress(output, &output_size, compressed, compressed_size) != Z_OK) return 2;
    if (output_size != sizeof(input) || memcmp(input, output, sizeof(input))) return 3;
    printf("zlib %s compressed=%lu crc=%08lx\n", zlibVersion(), compressed_size, crc32(0, output, sizeof(output)));
    return 0;
}