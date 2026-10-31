/* The services the portable host refuses by name (HostSyscalls.Unavailable) return the documented errno. */
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <sys/socket.h>
#include <sys/epoll.h>
#include <sys/shm.h>
#include <sys/stat.h>
static const char *name(int error) { return error == ENOSYS ? "ENOSYS" : error == EPERM ? "EPERM" : strerror(error); }
int main(void)
{
    errno = 0; if (fork() < 0) printf("fork %s\n", name(errno));
    errno = 0; if (link("a-file-that-does-not-exist", "another") < 0) printf("link %s\n", name(errno));
    errno = 0; if (socket(AF_INET, SOCK_STREAM, 0) < 0) printf("socket %s\n", name(errno));
    errno = 0; if (epoll_create1(0) < 0) printf("epoll_create1 %s\n", name(errno));
    errno = 0; if (shmget(1234, 4096, 0600) < 0) printf("shmget %s\n", name(errno));
    errno = 0; if (setsid() < 0) printf("setsid %s\n", name(errno));
    errno = 0; if (mkfifo("a-fifo", 0600) < 0) printf("mkfifo %s\n", name(errno));
    return 0;
}
