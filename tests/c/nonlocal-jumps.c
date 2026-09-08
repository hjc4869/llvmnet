#include <setjmp.h>
#include <stdio.h>

static jmp_buf outer;
static jmp_buf inner;
static volatile int visits;

__attribute__((noinline)) static void jump_outer(int value)
{
    ++visits;
    longjmp(outer, value);
}

static void (*volatile callback)(int) = jump_outer;
static void (*volatile jump_function)(jmp_buf, int) = longjmp;

__attribute__((noinline)) static void nested(void)
{
    volatile int preserved = 19;
    if (setjmp(inner) == 0)
        jump_outer(7);
    if (preserved != 19)
        jump_outer(99);
    jump_outer(8);
}

int main(void)
{
    volatile int preserved = 41;
    int value = setjmp(outer);
    if (value == 0)
        jump_outer(0);
    if (value == 1)
        callback(-3);
    if (value == -3)
        nested();
    if (value != 7 || visits != 3 || preserved != 41)
        return 1;
    value = setjmp(outer);
    if (value == 0)
        jump_function(outer, 11);
    if (value != 11 || visits != 3)
        return 2;
    puts("nonlocal jumps passed");
    return 0;
}