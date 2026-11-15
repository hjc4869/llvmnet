/* The program's own identity (TC-21): argv[0], /proc/self/exe through readlink, realpath, stat, lstat, access and open,
 * a child started through /proc/self/exe (a program re-executing itself, as Flang's driver does), and an environment
 * without the variables llvmnet's launcher passes to the runtime. tests/launcher-identity.sh starts it in many ways
 * and compares with the native build. "names" prints only argv[0] and the environment; "child" is the child's part. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

extern char **environ;

static void environment(const char *prefix) {
    static const char *const names[] = {"LLVMNET_ARGV0", "LLVMNET_EXE"};
    for (size_t index = 0; index < sizeof names / sizeof *names; index++)
        printf("%s%s: %s\n", prefix, names[index], getenv(names[index]) ? "set" : "unset");
}

static int same_file(const struct stat *a, const struct stat *b) {
    return a->st_dev == b->st_dev && a->st_ino == b->st_ino && S_ISREG(a->st_mode);
}

int main(int argc, char **argv) {
    char link[PATH_MAX];
    ssize_t length = readlink("/proc/self/exe", link, sizeof link - 1);
    if (length < 0) {
        printf("readlink: %s\n", strerror(errno));
        return 1;
    }
    link[length] = 0;
    if (argc > 1 && strcmp(argv[1], "child") == 0) {
        printf("child readlink: %s\n", link);
        environment("child ");
        return 0;
    }
    printf("argv[0]: %s\n", argv[0]);
    environment("");
    if (argc > 1 && strcmp(argv[1], "names") == 0) return 0;
    printf("readlink: %s\n", link);
    char real[PATH_MAX];
    printf("realpath: %s\n", realpath("/proc/self/exe", real) ? real : strerror(errno));
    struct stat target, followed, own, opened;
    if (stat(link, &target) != 0) {
        printf("stat of the target: %s\n", strerror(errno));
        return 1;
    }
    printf("stat: %s\n", stat("/proc/self/exe", &followed) == 0 && same_file(&followed, &target) ? "the executable" : "another file");
    printf("lstat: %s\n", lstat("/proc/self/exe", &own) == 0 && S_ISLNK(own.st_mode) ? "a link" : "not a link");
    printf("access: %s\n", access("/proc/self/exe", X_OK) == 0 ? "executable" : strerror(errno));
    int descriptor = open("/proc/self/exe", O_RDONLY | O_CLOEXEC);
    printf("open: %s\n", descriptor >= 0 && fstat(descriptor, &opened) == 0 && same_file(&opened, &target) ? "the executable" : "another file");
    if (descriptor >= 0) close(descriptor);
    char *child[] = {argv[0], "child", NULL};
    pid_t pid;
    int status;
    fflush(stdout);
    int error = posix_spawn(&pid, "/proc/self/exe", NULL, NULL, child, environ);
    if (error != 0) printf("spawn: %s\n", strerror(error));
    else if (waitpid(pid, &status, 0) != pid) printf("waitpid: %s\n", strerror(errno));
    else printf("spawn: exit %d\n", WIFEXITED(status) ? WEXITSTATUS(status) : -1);
    return 0;
}
