/* Aggregates through variadic calls (BE-19): x86-64 passes a small struct in registers, one per eightbyte, when they
 * all fit, and larger ones or those with long double in memory; a byval fixed parameter before the ellipsis takes
 * no register. The program prints the same natively and in both modes. */
#include <stdarg.h>
#include <stdio.h>

struct mixed { long integer; double real; };
struct reals { double first, second; };
struct triple { int a, b, c; };
struct halves { float a, b; };
struct big { char text[24]; int tail; };
struct extended { long double value; };
struct bytes { unsigned char data[3]; };

static void show(struct big label, int count, ...)
{
    va_list arguments;
    va_start(arguments, count);
    printf("%s:", label.text);
    for (int index = 0; index < count; index++) {
        int kind = va_arg(arguments, int);
        switch (kind) {
        case 0: { struct mixed value = va_arg(arguments, struct mixed); printf(" mixed(%ld,%g)", value.integer, value.real); break; }
        case 1: { struct reals value = va_arg(arguments, struct reals); printf(" reals(%g,%g)", value.first, value.second); break; }
        case 2: { struct triple value = va_arg(arguments, struct triple); printf(" triple(%d,%d,%d)", value.a, value.b, value.c); break; }
        case 3: { struct halves value = va_arg(arguments, struct halves); printf(" halves(%g,%g)", value.a, value.b); break; }
        case 4: { struct big value = va_arg(arguments, struct big); printf(" big(%s,%d)", value.text, value.tail); break; }
        case 5: { struct extended value = va_arg(arguments, struct extended); printf(" extended(%Lg)", value.value); break; }
        case 6: { struct bytes value = va_arg(arguments, struct bytes); printf(" bytes(%d,%d,%d)", value.data[0], value.data[1], value.data[2]); break; }
        case 7: printf(" int(%d)", va_arg(arguments, int)); break;
        case 8: printf(" double(%g)", va_arg(arguments, double)); break;
        case 9: printf(" ldouble(%Lg)", va_arg(arguments, long double)); break;
        }
    }
    va_end(arguments);
    printf("\n");
}

/* A variadic call in a loop: under the JIT the call area of such a function stays a localloc block, elsewhere it is a
 * frame slot (PF-24); after fifteen integers in memory, a long double that va_arg aligns to 16 bytes. */
static void repeat(int times)
{
    for (int round = 0; round < times; round++)
        show((struct big){ "in a loop", round }, 11, 8, 0.25, 7, round, 7, 2, 7, 3, 7, 4, 7, 5, 7, 6, 7, 7, 7, 8, 7, 9, 9, 0.5L + round);
}

int main(void)
{
    struct mixed mixed = { 7, 2.5 };
    struct reals reals = { 1.25, -3.5 };
    struct triple triple = { 1, 2, 3 };
    struct halves halves = { 0.5f, 0.75f };
    struct big big = { "twenty-four characters!", 42 };
    struct extended extended = { 1.0L / 3 };
    struct bytes bytes = { { 9, 8, 7 } };
    show((struct big){ "one of each", 1 }, 7, 0, mixed, 1, reals, 2, triple, 3, halves, 4, big, 5, extended, 6, bytes);
    /* Enough integers and doubles first that later aggregates no longer fit in registers. */
    show((struct big){ "after registers", 2 }, 12, 7, 1, 7, 2, 7, 3, 7, 4, 7, 5, 0, mixed, 1, reals, 2, triple, 3, halves, 4, big, 6, bytes, 8, 9.5);
    show((struct big){ "floating full", 3 }, 10, 8, 1.0, 8, 2.0, 8, 3.0, 8, 4.0, 8, 5.0, 8, 6.0, 8, 7.0, 1, reals, 0, mixed, 3, halves);
    show((struct big){ "scalars", 4 }, 5, 7, -1, 8, 0.125, 9, 2.0L, 7, 1000000, 2, triple);
    repeat(2);
    return 0;
}
