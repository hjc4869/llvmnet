/* Host interface conformance (plan item LC-14): results and errno values of the system services the portable
 * host layer provides, for success and error cases. The program runs natively and in portable mode in an
 * empty scratch directory; both outputs must be identical, because the host implements Linux semantics.
 * Values that legitimately differ between runs (times, sizes, ids) are reduced to facts that do not. */
#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <sys/random.h>
#include <sys/resource.h>
#include <sys/select.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <sys/time.h>
#include <sys/utsname.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

extern char **environ;

static const char *name(int error)
{
    switch (error) {
    case 0: return "0";
    case EPERM: return "EPERM";
    case ENOENT: return "ENOENT";
    case EINTR: return "EINTR";
    case EBADF: return "EBADF";
    case ECHILD: return "ECHILD";
    case EAGAIN: return "EAGAIN";
    case EACCES: return "EACCES";
    case EEXIST: return "EEXIST";
    case EXDEV: return "EXDEV";
    case ENOTDIR: return "ENOTDIR";
    case EISDIR: return "EISDIR";
    case EINVAL: return "EINVAL";
    case EMFILE: return "EMFILE";
    case ENOTTY: return "ENOTTY";
    case ESPIPE: return "ESPIPE";
    case EPIPE: return "EPIPE";
    case ERANGE: return "ERANGE";
    case ENAMETOOLONG: return "ENAMETOOLONG";
    case ENOSYS: return "ENOSYS";
    case ENOTEMPTY: return "ENOTEMPTY";
    case ELOOP: return "ELOOP";
    default: return "other";
    }
}

/* Prints the result and, for -1, the errno name. */
#define CHECK(label, expression) do { \
        errno = 0; \
        long result_ = (long)(expression); \
        printf("%-44s %ld %s\n", label, result_, result_ == -1 ? name(errno) : "-"); \
    } while (0)
#define FACT(label, condition) printf("%-44s %s\n", label, (condition) ? "yes" : "no")

static void write_file(const char *path, const char *text)
{
    FILE *file = fopen(path, "w");
    fputs(text, file);
    fclose(file);
}

static void files(void)
{
    char buffer[64];
    write_file("file", "abcdef");
    mkdir("dir", 0755);
    CHECK("open missing", open("missing", O_RDONLY));
    CHECK("open directory for writing", open("dir", O_WRONLY));
    CHECK("open below a file", open("file/x", O_RDONLY));
    CHECK("open existing with O_EXCL", open("file", O_WRONLY | O_CREAT | O_EXCL, 0644));
    CHECK("open file with O_DIRECTORY", open("file", O_RDONLY | O_DIRECTORY));
    CHECK("read bad descriptor", read(99, buffer, 1));
    CHECK("write bad descriptor", write(99, buffer, 1));
    CHECK("close bad descriptor", close(99));
    int reader = open("file", O_RDONLY);
    CHECK("write to read-only descriptor", write(reader, "x", 1));
    CHECK("ftruncate read-only descriptor", ftruncate(reader, 1));
    CHECK("lseek negative", lseek(reader, -1, SEEK_SET));
    CHECK("lseek end", lseek(reader, 0, SEEK_END));
    CHECK("pread at offset 2", pread(reader, buffer, 3, 2));
    FACT("pread data", memcmp(buffer, "cde", 3) == 0);
    CHECK("position unchanged by pread", lseek(reader, 0, SEEK_CUR));
    CHECK("isatty on a file", isatty(reader));
    struct winsize size;
    CHECK("TIOCGWINSZ on a file", ioctl(reader, TIOCGWINSZ, &size));
    CHECK("dup2 bad source", dup2(99, 20));
    CHECK("dup2 to 20", dup2(reader, 20));
    CHECK("read through the duplicate", read(20, buffer, 2));
    CHECK("shared offset after the read", lseek(reader, 0, SEEK_CUR));
    CHECK("close duplicate", close(20));
    CHECK("F_DUPFD at least 30", fcntl(reader, F_DUPFD, 30));
    CHECK("F_GETFD", fcntl(reader, F_GETFD));
    CHECK("F_SETFD FD_CLOEXEC", fcntl(reader, F_SETFD, FD_CLOEXEC));
    CHECK("F_GETFD after F_SETFD", fcntl(reader, F_GETFD));
    CHECK("F_GETFL access mode", fcntl(reader, F_GETFL) & O_ACCMODE);
    close(30);
    close(reader);
    int directory = open("dir", O_RDONLY | O_DIRECTORY);
    CHECK("read a directory descriptor", read(directory, buffer, 1));
    close(directory);
    int writer = open("file", O_WRONLY | O_APPEND);
    CHECK("read write-only descriptor", read(writer, buffer, 1));
    CHECK("F_GETFL has O_APPEND", (fcntl(writer, F_GETFL) & O_APPEND) != 0);
    CHECK("append write", write(writer, "gh", 2));
    CHECK("pwrite at 0 with O_APPEND", pwrite(writer, "Z", 1, 0));
    close(writer);
    int updater = open("file", O_RDWR);
    CHECK("pwrite at 1", pwrite(updater, "B", 1, 1));
    CHECK("pread whole file", pread(updater, buffer, sizeof buffer, 0));
    buffer[9] = 0;
    printf("%-44s %.9s\n", "file contents", buffer);
    CHECK("ftruncate to 4", ftruncate(updater, 4));
    CHECK("ftruncate negative", ftruncate(updater, -1));
    close(updater);
    CHECK("truncate path to 3", truncate("file", 3));
    struct stat metadata;
    stat("file", &metadata);
    CHECK("size after truncate", metadata.st_size);
}

static void pipes(void)
{
    int ends[2];
    char buffer[16];
    CHECK("pipe", pipe(ends));
    CHECK("lseek on a pipe", lseek(ends[0], 0, SEEK_SET));
    CHECK("pread on a pipe", pread(ends[0], buffer, 1, 0));
    struct pollfd watch = {ends[0], POLLIN, 0};
    CHECK("poll empty pipe", poll(&watch, 1, 0));
    CHECK("write to pipe", write(ends[1], "hello", 5));
    CHECK("poll readable pipe", poll(&watch, 1, 1000));
    FACT("POLLIN reported", watch.revents & POLLIN);
    fd_set readable;
    FD_ZERO(&readable);
    FD_SET(ends[0], &readable);
    struct timeval zero = {0, 0};
    CHECK("select readable pipe", select(ends[0] + 1, &readable, 0, 0, &zero));
    int available = 0;
    CHECK("FIONREAD", ioctl(ends[0], FIONREAD, &available));
    CHECK("bytes available", available);
    CHECK("read from pipe", read(ends[0], buffer, sizeof buffer));
    CHECK("close write end", close(ends[1]));
    CHECK("read at end of pipe", read(ends[0], buffer, sizeof buffer));
    CHECK("poll at end of pipe", poll(&watch, 1, 0));
    FACT("POLLHUP reported", watch.revents & POLLHUP);
    close(ends[0]);
    signal(SIGPIPE, SIG_IGN);
    CHECK("pipe2 O_CLOEXEC", pipe2(ends, O_CLOEXEC));
    CHECK("F_GETFD of pipe2 end", fcntl(ends[0], F_GETFD));
    close(ends[0]);
    CHECK("write without a reader", write(ends[1], "x", 1));
    close(ends[1]);
    CHECK("pipe2 O_NONBLOCK", pipe2(ends, O_NONBLOCK));
    CHECK("read empty non-blocking pipe", read(ends[0], buffer, 1));
    close(ends[0]);
    close(ends[1]);
}

static int names(const void *left, const void *right) { return strcmp(*(char *const *)left, *(char *const *)right); }

static void filesystem(void)
{
    struct stat metadata;
    char buffer[PATH_MAX];
    CHECK("stat missing", stat("missing", &metadata));
    CHECK("stat below a file", stat("file/x", &metadata));
    CHECK("mkdir existing", mkdir("dir", 0755));
    CHECK("mkdir in missing parent", mkdir("missing/dir", 0755));
    CHECK("mkdir below a file", mkdir("file/dir", 0755));
    write_file("dir/inner", "x");
    CHECK("rmdir non-empty", rmdir("dir"));
    CHECK("rmdir a file", rmdir("file"));
    CHECK("rmdir missing", rmdir("missing"));
    CHECK("unlink a directory", unlink("dir"));
    CHECK("unlink missing", unlink("missing"));
    CHECK("rename missing", rename("missing", "other"));
    CHECK("rename file over non-empty directory", rename("file", "dir"));
    mkdir("empty", 0755);
    CHECK("rename directory over a file", rename("empty", "file"));
    CHECK("rename file over a file", rename("dir/inner", "moved"));
    CHECK("access moved file", access("moved", F_OK));
    CHECK("access missing", access("missing", F_OK));
    CHECK("chdir to a file", chdir("file"));
    CHECK("chdir missing", chdir("missing"));
    CHECK("getcwd with a 1-byte buffer", getcwd(buffer, 1) ? 0 : -1);
    CHECK("chdir dir", chdir("dir"));
    FACT("getcwd ends in /dir", getcwd(buffer, sizeof buffer) && strcmp(strrchr(buffer, '/'), "/dir") == 0);
    int parent = open("..", O_RDONLY | O_DIRECTORY);
    CHECK("fchdir to parent", fchdir(parent));
    close(parent);
    CHECK("chmod 0600", chmod("moved", 0600));
    stat("moved", &metadata);
    CHECK("mode after chmod", metadata.st_mode & 07777);
    FACT("regular file", S_ISREG(metadata.st_mode));
    CHECK("symlink", symlink("moved", "link"));
    CHECK("symlink over an existing name", symlink("moved", "link"));
    ssize_t length = readlink("link", buffer, sizeof buffer);
    CHECK("readlink", length);
    printf("%-44s %.*s\n", "link target", (int)(length > 0 ? length : 0), buffer);
    CHECK("readlink a regular file", readlink("moved", buffer, sizeof buffer));
    CHECK("lstat link", lstat("link", &metadata));
    FACT("lstat reports a link", S_ISLNK(metadata.st_mode));
    CHECK("stat through link", stat("link", &metadata));
    FACT("stat reports the target", S_ISREG(metadata.st_mode));
    CHECK("open link with O_NOFOLLOW", open("link", O_RDONLY | O_NOFOLLOW));
    CHECK("symlink to missing target", symlink("nowhere", "dangling"));
    CHECK("stat dangling link", stat("dangling", &metadata));
    CHECK("unlink link", unlink("link"));
    CHECK("access moved after unlinking the link", access("moved", F_OK));
    FACT("realpath resolves dir/..", realpath("dir/../moved", buffer) && strcmp(strrchr(buffer, '/'), "/moved") == 0);
    CHECK("realpath missing", realpath("missing", buffer) ? 0 : -1);
    struct statvfs volume;
    CHECK("statvfs", statvfs(".", &volume));
    mkdir("list", 0755);
    write_file("list/b", "");
    write_file("list/a", "");
    mkdir("list/c", 0755);
    DIR *stream = opendir("list");
    char *entries[8];
    int count = 0;
    for (struct dirent *entry; (entry = readdir(stream)) && count < 8;)
        entries[count++] = strdup(entry->d_name);
    closedir(stream);
    qsort(entries, count, sizeof *entries, names);
    printf("%-44s", "directory entries");
    for (int index = 0; index < count; index++) printf(" %s", entries[index]);
    printf("\n");
    mkdir("locked", 0755);
    write_file("locked/file", "x");
    chmod("locked", 0);
    /* EACCES for an unprivileged user; a root user may search anyway, natively and in the host alike. */
    CHECK("stat below an unsearchable directory", stat("locked/file", &metadata));
    CHECK("open below an unsearchable directory", open("locked/file", O_RDONLY));
    chmod("locked", 0755);
    CHECK("symlink to a directory", symlink("list", "dirlink"));
    CHECK("stat through a directory link", stat("dirlink", &metadata));
    FACT("the link's target is a directory", S_ISDIR(metadata.st_mode));
    CHECK("stat of missing/.. (resolved in order)", stat("list/missing/..", &metadata));
    CHECK("stat of a/file/.. (file is not a directory)", stat("list/a/..", &metadata));
    CHECK("stat of dirlink/.. (link followed first)", stat("dirlink/..", &metadata));
    chmod("list", 0300);
    /* EACCES for an unprivileged user, as in the case above. */
    CHECK("opendir of an unreadable directory", opendir("list") ? 0 : -1);
    chmod("list", 0755);
    CHECK("opendir a file", opendir("moved") ? 0 : -1);
    CHECK("opendir missing", opendir("missing") ? 0 : -1);
}

static void memory(void)
{
    char *anonymous = mmap(0, 1 << 20, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    FACT("anonymous mapping", anonymous != MAP_FAILED);
    FACT("anonymous memory is zeroed", anonymous[12345] == 0);
    anonymous[0] = 1;
    anonymous[(1 << 20) - 1] = 2;
    CHECK("munmap middle pages", munmap(anonymous + 4096 * 8, 4096 * 4));
    CHECK("mprotect", mprotect(anonymous, 4096, PROT_READ));
    CHECK("madvise", madvise(anonymous, 4096, MADV_DONTNEED));
    CHECK("munmap the rest", munmap(anonymous, 1 << 20));
    CHECK("mmap zero length", mmap(0, 0, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0) == MAP_FAILED ? -1 : 0);
    CHECK("mmap bad descriptor", mmap(0, 4096, PROT_READ, MAP_PRIVATE, 99, 0) == MAP_FAILED ? -1 : 0);
    write_file("mapped", "0123456789");
    int descriptor = open("mapped", O_RDWR);
    char *view = mmap(0, 10, PROT_READ | PROT_WRITE, MAP_PRIVATE, descriptor, 0);
    FACT("private file mapping reads the file", view != MAP_FAILED && memcmp(view, "0123456789", 10) == 0);
    view[0] = 'X';
    munmap(view, 10);
    char buffer[10];
    pread(descriptor, buffer, 10, 0);
    FACT("private changes stay private", buffer[0] == '0');
    view = mmap(0, 10, PROT_READ | PROT_WRITE, MAP_SHARED, descriptor, 0);
    view[1] = 'Y';
    CHECK("msync", msync(view, 10, MS_SYNC));
    pread(descriptor, buffer, 10, 0);
    FACT("shared changes reach the file", buffer[1] == 'Y');
    view[2] = 'Z';
    munmap(view, 10);
    pread(descriptor, buffer, 10, 0);
    FACT("shared changes written at munmap", buffer[2] == 'Z');
    close(descriptor);
}

static volatile sig_atomic_t received;
static void handler(int signal) { received = signal; }

static void process(void)
{
    FACT("getpid positive", getpid() > 0);
    FACT("getppid positive", getppid() > 0);
    CHECK("kill self with signal 0", kill(getpid(), 0));
    struct sigaction action = {0};
    action.sa_handler = handler;
    CHECK("sigaction SIGUSR1", sigaction(SIGUSR1, &action, 0));
    CHECK("raise SIGUSR1", raise(SIGUSR1));
    CHECK("handler ran", received);
    CHECK("sigaction SIGKILL", sigaction(SIGKILL, &action, 0));
    signal(SIGUSR2, SIG_IGN);
    CHECK("raise ignored SIGUSR2", raise(SIGUSR2));
    sigset_t blocked, previous;
    sigemptyset(&blocked);
    sigaddset(&blocked, SIGUSR1);
    CHECK("sigprocmask block SIGUSR1", sigprocmask(SIG_BLOCK, &blocked, &previous));
    sigprocmask(SIG_SETMASK, 0, &previous);
    FACT("SIGUSR1 reported blocked", sigismember(&previous, SIGUSR1));
    sigprocmask(SIG_UNBLOCK, &blocked, 0);
    int status = system("exit 3");
    FACT("system exit status 3", WIFEXITED(status) && WEXITSTATUS(status) == 3);
    FACT("system reports a shell", system(0) != 0);
    FILE *command = popen("echo hello from a child", "r");
    char line[64] = "";
    FACT("popen read", command && fgets(line, sizeof line, command) && strcmp(line, "hello from a child\n") == 0);
    CHECK("pclose status", pclose(command));
    command = popen("cat > popen.txt", "w");
    fputs("written through popen\n", command);
    CHECK("pclose writer", pclose(command));
    FILE *result = fopen("popen.txt", "r");
    FACT("popen writer output", result && fgets(line, sizeof line, result) && strcmp(line, "written through popen\n") == 0);
    if (result) fclose(result);
    pid_t child;
    char *arguments[] = {"sh", "-c", "exit 7", 0};
    CHECK("posix_spawn", posix_spawn(&child, "/bin/sh", 0, 0, arguments, environ));
    CHECK("waitpid child", waitpid(child, &status, 0) == child ? 0 : -1);
    FACT("child exit status 7", WIFEXITED(status) && WEXITSTATUS(status) == 7);
    CHECK("waitpid without children", waitpid(-1, &status, WNOHANG));
    char *missing[] = {"missing-program", 0};
    CHECK("posix_spawnp missing program", posix_spawnp(&child, "llvmnet-missing-program", 0, 0, missing, environ));
    struct rlimit limit;
    CHECK("getrlimit RLIMIT_NOFILE", getrlimit(RLIMIT_NOFILE, &limit));
    struct utsname host;
    CHECK("uname", uname(&host));
    FACT("uname sysname", host.sysname[0] != 0);
    char hostname[256];
    CHECK("gethostname", gethostname(hostname, sizeof hostname));
    FACT("getpwuid of the current user", getpwuid(getuid()) != 0);
    FACT("sysconf processors", sysconf(_SC_NPROCESSORS_ONLN) > 0);
    CHECK("sysconf page size", sysconf(_SC_PAGESIZE));
    unsigned char random[16];
    CHECK("getrandom", getrandom(random, sizeof random, 0));
}

static void clocks(void)
{
    struct timespec first, second;
    CHECK("clock_gettime MONOTONIC", clock_gettime(CLOCK_MONOTONIC, &first));
    struct timespec pause = {0, 2000000};
    CHECK("nanosleep 2 ms", nanosleep(&pause, 0));
    clock_gettime(CLOCK_MONOTONIC, &second);
    FACT("monotonic clock advanced 2 ms", (second.tv_sec - first.tv_sec) * 1000000000L + second.tv_nsec - first.tv_nsec >= 2000000);
    CHECK("clock_gettime bad clock", clock_gettime(99, &first));
    struct timespec negative = {-1, 0};
    CHECK("nanosleep negative", nanosleep(&negative, 0));
    CHECK("clock_gettime PROCESS_CPUTIME_ID", clock_gettime(CLOCK_PROCESS_CPUTIME_ID, &first));
    CHECK("clock_gettime THREAD_CPUTIME_ID", clock_gettime(CLOCK_THREAD_CPUTIME_ID, &first));
    struct timeval now;
    CHECK("gettimeofday", gettimeofday(&now, 0));
    FACT("time agrees with gettimeofday", labs(time(0) - now.tv_sec) <= 1);
    time_t moment = 1700000000;
    char text[64];
    setenv("TZ", "EST5EDT,M3.2.0,M11.1.0", 1);
    tzset();
    strftime(text, sizeof text, "%Y-%m-%d %H:%M:%S %Z", localtime(&moment));
    printf("%-44s %s\n", "localtime with a POSIX TZ rule", text);
    if (access("/usr/share/zoneinfo/Europe/Berlin", F_OK) == 0) {
        setenv("TZ", "Europe/Berlin", 1);
        tzset();
        moment = 1690000000;
        strftime(text, sizeof text, "%Y-%m-%d %H:%M:%S %Z", localtime(&moment));
        printf("%-44s %s\n", "localtime with zoneinfo", text);
    }
}

int main(void)
{
    setvbuf(stdout, 0, _IONBF, 0);
    files();
    pipes();
    filesystem();
    memory();
    process();
    clocks();
    return 0;
}
