#include <stdio.h>
#include <unistd.h>
#include <errno.h>
static int state;
__attribute__((constructor(101))) static void first(void) { state = 7; }
__attribute__((constructor(102))) static void second(void) { state = state * 6; }
__attribute__((destructor(101))) static void last(void) { puts("destructor-last"); }
__attribute__((destructor(102))) static void before_last(void) { puts("destructor-first"); }
int main(void)
{
    unsigned char random[257];
    if (state != 42 || getentropy(random, 32)) return 1;
    errno = 0;
    if (getentropy(random, sizeof(random)) != -1 || errno != EIO) return 2;
    puts("main=42");
    return 0;
}