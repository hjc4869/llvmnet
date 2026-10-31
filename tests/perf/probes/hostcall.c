// Host-call cost (PF-03): 2 million getpid system calls, 1-byte writes, 4,096-byte writes and fputc calls, timed
// inside the program, to /dev/null or to the file named by argv[1]. Prints nanoseconds per call.
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>
static double now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec + t.tv_nsec * 1e-9; }
int main(int argc, char **argv)
{
    long n = 2000000;
    int fd = open(argc > 1 ? argv[1] : "/dev/null", O_WRONLY | O_CREAT | O_TRUNC, 0644);
    double t0 = now();
    for (long i = 0; i < n; i++) syscall(SYS_getpid);
    double t1 = now();
    for (long i = 0; i < n; i++) write(fd, "x", 1);
    double t2 = now();
    char block[4096] = {0};
    for (long i = 0; i < n / 4; i++) write(fd, block, sizeof block);
    double t3 = now();
    FILE *f = fopen(argc > 1 ? argv[1] : "/dev/null", "w");
    for (long i = 0; i < n * 4; i++) fputc('x', f);
    fclose(f);
    double t4 = now();
    printf("getpid %.0f ns, write(1) %.0f ns, write(4096) %.0f ns, fputc %.1f ns per call\n",
           (t1 - t0) / n * 1e9, (t2 - t1) / n * 1e9, (t3 - t2) / (n / 4) * 1e9, (t4 - t3) / (n * 4) * 1e9);
    return 0;
}
