/* Child processes in system mode. The fork family runs the classic pattern (dup2 the pipe onto stdout, close, execv)
   and a child that computes and exits; posix_spawn starts the same program without duplicating the process. A refused
   call prints its errno, so the output equals native wherever fork is available. */
#define _GNU_SOURCE
#include <errno.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

extern char **environ;
pid_t _Fork(void);

static void report(const char *name, int fds[2], pid_t pid)
{
    char text[64] = {0};
    close(fds[1]);
    ssize_t length = read(fds[0], text, sizeof text - 1);
    close(fds[0]);
    int status = 0;
    if (waitpid(pid, &status, 0) != pid) {
        printf("%s: waitpid %s\n", name, strerror(errno));
        return;
    }
    if (length > 0 && text[length - 1] == '\n') text[length - 1] = 0;
    printf("%s: read '%s', exit %d\n", name, text, WIFEXITED(status) ? WEXITSTATUS(status) : -1);
}

/* The call is made here, not in a helper: a vfork child must not return from the function that called vfork. */
static void exec_child(const char *name)
{
    int fds[2];
    if (pipe(fds)) return;
    errno = 0;
    pid_t pid = name[0] == 'v' ? vfork() : name[0] == '_' ? _Fork() : fork();
    if (pid == 0) {
        if (dup2(fds[1], 1) < 0) _exit(126);
        close(fds[0]);
        close(fds[1]);
        char *arguments[] = {"/bin/echo", "child says hi", NULL};
        execv(arguments[0], arguments);
        _exit(127);
    }
    if (pid < 0) {
        printf("%s: %s\n", name, errno == ENOSYS ? "ENOSYS" : strerror(errno));
        close(fds[0]);
        close(fds[1]);
        return;
    }
    report(name, fds, pid);
}

int main(void)
{
    exec_child("fork");
    exec_child("vfork");
    exec_child("_Fork");

    /* A child that does work of its own and reports through its exit status, as cppcheck's workers do. */
    errno = 0;
    pid_t pid = fork();
    if (pid == 0) {
        int sum = 0;
        for (int i = 1; i <= 10; i++) sum += i;
        _exit(sum);
    }
    if (pid < 0) printf("fork worker: %s\n", errno == ENOSYS ? "ENOSYS" : strerror(errno));
    else {
        int status = 0;
        waitpid(pid, &status, 0);
        printf("fork worker: exit %d\n", WIFEXITED(status) ? WEXITSTATUS(status) : -1);
    }

    int fds[2];
    if (pipe(fds)) return 1;
    posix_spawn_file_actions_t actions;
    posix_spawn_file_actions_init(&actions);
    posix_spawn_file_actions_adddup2(&actions, fds[1], 1);
    posix_spawn_file_actions_addclose(&actions, fds[0]);
    posix_spawn_file_actions_addclose(&actions, fds[1]);
    char *arguments[] = {"/bin/echo", "child says hi", NULL};
    int error = posix_spawn(&pid, arguments[0], &actions, NULL, arguments, environ);
    posix_spawn_file_actions_destroy(&actions);
    if (error) printf("posix_spawn: %s\n", strerror(error));
    else report("posix_spawn", fds, pid);
    return 0;
}
