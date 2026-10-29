/* Indirect-call signature policy (BE-18); see docs/dev/calling-convention.md. */
#include <string.h>
typedef int (*int_function)(int);
static long widen(long value) { return value + 1; }
static int add_one(int value) { return value + 1; }
static int first_byte(const char *text) { return text[0]; }
int main(int argc, char **argv)
{
    int (*through_void)(void *) = (int (*)(void *))first_byte;  /* pointer types: same CIL signature */
    char byte = 5;
    if (through_void(&byte) != 5) return 1;
    int_function direct = add_one;
    if (direct(1) != 2) return 2;
    if (argc > 1 && strcmp(argv[1], "mismatch") == 0) {
        int_function wrong = (int_function)widen;           /* long(long) called as int(int) */
        return wrong(41) == 42 ? 0 : 3;
    }
    return 0;
}
