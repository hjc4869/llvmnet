#include <stdio.h>
#include <stdint.h>

uint8_t *input_address(void);
uint8_t *output_address(void);
int run(void);

int main(int argc, char **argv)
{
    if (argc != 2)
        return 2;
    FILE *input = fopen(argv[1], "rb");
    if (!input)
        return 2;
    int record = 0;
    size_t count;
    while ((count = fread(input_address(), 1, 128, input)) == 128) {
        int operations = run();
        for (int operation = 0; operation < operations; operation++) {
            printf("%d:%d ", record, operation);
            for (int index = 0; index < 16; index++)
                printf("%02X", output_address()[operation * 16 + index]);
            putchar('\n');
        }
        record++;
    }
    int failed = count != 0 || ferror(input) || record == 0;
    fclose(input);
    return failed;
}