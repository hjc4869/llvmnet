/* A signal sent from outside the process reaches its handler: the program reports that it is
 * ready, waits for SIGTERM and exits normally from the handler's flag; the default action would end it with 143. */
#include <signal.h>
#include <stdio.h>
#include <time.h>
#include <unistd.h>

static volatile sig_atomic_t received;
static void handler(int signal) { received = signal; }

int main(void)
{
    struct sigaction action = {0};
    action.sa_handler = handler;
    sigaction(SIGTERM, &action, 0);
    printf("ready\n");
    fflush(stdout);
    struct timespec pause = {0, 10000000};
    for (int tick = 0; tick < 3000 && !received; tick++) nanosleep(&pause, 0);
    printf("received signal %d\n", received);
    return received == SIGTERM ? 0 : 1;
}
