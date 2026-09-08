#include <setjmp.h>
#include <signal.h>
#include <stdio.h>

int main(void)
{
    sigset_t original;
    sigset_t blocked;
    sigset_t current;
    sigjmp_buf saved;
    if (sigemptyset(&blocked) || sigaddset(&blocked, SIGUSR1) ||
        sigprocmask(SIG_BLOCK, &blocked, &original))
        return 1;
    if (sigsetjmp(saved, 1) == 0) {
        if (sigprocmask(SIG_UNBLOCK, &blocked, 0))
            return 2;
        siglongjmp(saved, 0);
    }
    if (sigprocmask(SIG_BLOCK, 0, &current) || sigismember(&current, SIGUSR1) != 1)
        return 3;
    if (sigsetjmp(saved, 0) == 0) {
        if (sigprocmask(SIG_UNBLOCK, &blocked, 0))
            return 4;
        siglongjmp(saved, 2);
    }
    if (sigprocmask(SIG_BLOCK, 0, &current) || sigismember(&current, SIGUSR1) != 0)
        return 5;
    if (sigprocmask(SIG_SETMASK, &original, 0))
        return 6;
    puts("nonlocal jump signal masks passed");
    return 0;
}