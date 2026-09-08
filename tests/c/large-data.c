#include <stddef.h>
#include <stdio.h>

static const unsigned char table[5242881] = {
    [0] = 1, [1048575] = 2, [1048576] = 3, [2097151] = 4,
    [2097152] = 5, [4194303] = 6, [4194304] = 7, [5242880] = 8
};
static const volatile unsigned char *locations[] = { table, table + 1048576, table + 4194304, table + 5242880 };

int main(void)
{
    unsigned long total = 0;
    const volatile unsigned char *bytes = table;
    for (size_t index = 0; index < sizeof(table); ++index)
        total += bytes[index];
    if (total != 36 || *locations[0] != 1 || *locations[1] != 3 || *locations[2] != 7 || *locations[3] != 8)
        return 1;
    puts("large initialized data: all bytes, chunk boundaries and relocations passed");
    return 0;
}