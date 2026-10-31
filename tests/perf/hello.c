// Startup latency (PF-10): the whole program is one line of output, so its time is process startup and exit.
#include <stdio.h>

int main(void)
{
    puts("hello, world");
    return 0;
}
