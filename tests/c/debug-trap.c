#include <signal.h>
#include <stdio.h>

static volatile sig_atomic_t trapped;

static void handler(int signal_number)
{
    trapped = signal_number;
}

int main(void)
{
    if (signal(SIGTRAP, handler) == SIG_ERR)
        return 1;
    __asm__ volatile("int3");
    if (trapped != SIGTRAP)
        return 2;
    puts("debug trap signal delivered");
    return 0;
}