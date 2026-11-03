/* Host interface conformance: results and errno values of the system services the portable
 * host layer provides, for success and error cases. The program runs natively and in portable mode in an
 * empty scratch directory; both outputs must be identical, because the host implements Linux semantics.
 * Values that legitimately differ between runs (times, sizes, ids) are reduced to facts that do not. */
#define _GNU_SOURCE
#include <dirent.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <sys/sendfile.h>
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
#include <sys/uio.h>
#include <sys/utsname.h>
#include <sys/wait.h>
#include <time.h>
#include <pthread.h>
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
    /* O_CREAT creates the file with read-only access too, whatever its mode bits; the descriptor still only reads. */
    int created = open("created", O_RDONLY | O_CREAT | O_EXCL, 0);
    FACT("create read-only, mode 0, with O_EXCL", created >= 0);
    CHECK("write to the created read-only descriptor", write(created, "x", 1));
    close(created);
    unlink("created");
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
    /* sendfile and copy_file_range, which std::filesystem::copy_file uses. */
    write_file("source", "0123456789");
    int source = open("source", O_RDONLY), target = open("target", O_WRONLY | O_CREAT | O_TRUNC, 0644);
    off_t offset = 2;
    CHECK("sendfile from offset 2", sendfile(target, source, &offset, 5));
    CHECK("offset advanced", offset);
    CHECK("input position unchanged", lseek(source, 0, SEEK_CUR));
    CHECK("sendfile from the position", sendfile(target, source, NULL, 3));
    CHECK("input position advanced", lseek(source, 0, SEEK_CUR));
    off_t input = 8, output = 0;
    CHECK("copy_file_range at offsets", copy_file_range(source, &input, target, NULL, 100, 0));
    CHECK("copy_file_range with flags", copy_file_range(source, &input, target, &output, 1, 1));
    CHECK("sendfile to a read-only descriptor", sendfile(source, source, NULL, 1));
    close(target);
    target = open("target", O_RDONLY);
    CHECK("copied length", read(target, buffer, sizeof buffer));
    printf("%-44s %.10s\n", "copied contents", buffer);
    close(target);
    close(source);
    int ends[2];
    pipe(ends);
    write(ends[1], "xy", 2);
    target = open("target", O_WRONLY);
    CHECK("sendfile from a pipe", sendfile(target, ends[0], NULL, 2));
    close(target);
    close(ends[0]);
    close(ends[1]);
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

static char pattern(long index) { return (char)('a' + index % 23); }

struct record_writer { int descriptor; char letter; };

/* Writes 100 records of 1000 bytes, each as a 1-byte header and a 999-byte body in one writev. */
static void *write_records(void *argument)
{
    struct record_writer *writer = argument;
    char header = writer->letter, body[999];
    memset(body, writer->letter, sizeof body);
    struct iovec parts[2] = {{&header, 1}, {body, sizeof body}};
    for (int index = 0; index < 100; index++)
        if (writev(writer->descriptor, parts, 2) != 1000) return (void *)1;
    return 0;
}

/* readv, writev, preadv and pwritev make one transfer whatever the number of vectors: a pipe write of at most PIPE_BUF
 * bytes is atomic, counts are partial where one write's would be, and a read returns what a pipe holds. Linux counts
 * pipe space in pages, so the pipe checks fill whole pages or leave room in the last one. */
static void vectors(void)
{
    static char data[160000], back[160000], records[200000];
    for (long index = 0; index < (long)sizeof data; index++) data[index] = pattern(index);
    int file = open("vectors", O_RDWR | O_CREAT | O_TRUNC, 0644);
    struct iovec three[3] = {{data, 3}, {data + 3, 70000}, {data + 70003, 5}};
    CHECK("writev 3, 70000 and 5 bytes", writev(file, three, 3));
    struct iovec small[40];
    for (int index = 0; index < 40; index++) small[index] = (struct iovec){data + 70008 + index * 2000, 2000};
    CHECK("writev 40 vectors of 2000 bytes", writev(file, small, 40));
    CHECK("position after writev", lseek(file, 0, SEEK_CUR));
    CHECK("pread the vectors back", pread(file, back, sizeof back, 0));
    FACT("file holds the vectors in order", memcmp(back, data, 150008) == 0);
    lseek(file, 0, SEEK_SET);
    memset(back, 0, sizeof back);
    struct iovec refill[2] = {{back, 65535}, {back + 65535, 4096}};
    CHECK("readv 65535 and 4096 bytes", readv(file, refill, 2));
    struct iovec pieces[3] = {{back + 69631, 10}, {back + 69641, 100}, {back + 69741, 1000}};
    CHECK("readv 10, 100 and 1000 bytes", readv(file, pieces, 3));
    FACT("readv fills the vectors in order", memcmp(back, data, 70741) == 0);
    CHECK("position after readv", lseek(file, 0, SEEK_CUR));
    struct iovec tail[2] = {{back, 5}, {back + 5, 100}};
    CHECK("preadv 7 bytes before the end", preadv(file, tail, 2, 150001));
    FACT("preadv data", memcmp(back, data + 150001, 7) == 0);
    CHECK("position unchanged by preadv", lseek(file, 0, SEEK_CUR));
    struct iovec patch[2] = {{(void *)"XY", 2}, {(void *)"Z", 1}};
    CHECK("pwritev 3 bytes at 1", pwritev(file, patch, 2, 1));
    CHECK("pread the patch", pread(file, back, 5, 0));
    printf("%-44s %.5s\n", "patched contents", back);
    CHECK("readv no vectors", readv(file, pieces, 0));
    CHECK("readv no vectors, bad descriptor", readv(99, pieces, 0));
    CHECK("writev 1025 vectors", writev(file, small, 1025));
    struct iovec huge = {data, (size_t)-1};
    CHECK("writev a vector of SIZE_MAX bytes", writev(file, &huge, 1));
    close(file);
    unlink("vectors");
    int zero = open("/dev/zero", O_RDONLY);
    struct iovec zeros[2] = {{back, 70000}, {back + 70000, 1000}};
    CHECK("readv 70000 and 1000 bytes from /dev/zero", readv(zero, zeros, 2));
    close(zero);

    int ends[2];
    pipe2(ends, O_NONBLOCK);
    CHECK("fill a pipe but 100 bytes", write(ends[1], data, 65436));
    struct iovec two[2] = {{data, 100}, {data + 100, 100}};
    CHECK("writev 200 bytes into 100 free", writev(ends[1], two, 2));
    struct iovec halves[2] = {{data, 50}, {data + 50, 50}};
    CHECK("writev 100 bytes into 100 free", writev(ends[1], halves, 2));
    while (read(ends[0], back, sizeof back) > 0) {}
    CHECK("fill a pipe but one page", write(ends[1], data, 61440));
    struct iovec pages[3] = {{data, 4000}, {data + 4000, 4000}, {data + 8000, 4000}};
    CHECK("writev 12000 bytes into 4096 free", writev(ends[1], pages, 3));
    close(ends[0]);
    close(ends[1]);

    pipe(ends);
    write(ends[1], "abcdefghij", 10);
    char first[4], second[100];
    struct iovec split[2] = {{first, sizeof first}, {second, sizeof second}};
    CHECK("readv 10 bytes into 4 and 100", readv(ends[0], split, 2));
    FACT("readv splits the bytes in order", memcmp(first, "abcd", 4) == 0 && memcmp(second, "efghij", 6) == 0);
    write(ends[1], "wxyz", 4);
    CHECK("readv returns what the pipe holds", readv(ends[0], split, 2));
    CHECK("preadv on a pipe", preadv(ends[0], split, 2, 0));
    pthread_t threads[2];
    struct record_writer writers[2] = {{ends[1], 'A'}, {ends[1], 'B'}};
    for (int index = 0; index < 2; index++) pthread_create(&threads[index], 0, write_records, &writers[index]);
    long received = 0, got;
    while (received < (long)sizeof records && (got = read(ends[0], records + received, sizeof records - received)) > 0) received += got;
    void *failed[2];
    for (int index = 0; index < 2; index++) pthread_join(threads[index], &failed[index]);
    int whole = received == (long)sizeof records && !failed[0] && !failed[1], letters[2] = {0, 0};
    for (long record = 0; whole && record < (long)sizeof records / 1000; record++) {
        const char *start = records + record * 1000;
        whole = (*start == 'A' || *start == 'B') && memchr(start, *start == 'A' ? 'B' : 'A', 1000) == 0;
        letters[*start == 'B']++;
    }
    FACT("concurrent writev records stay whole", whole && letters[0] == 100 && letters[1] == 100);
    close(ends[0]);
    CHECK("writev without a reader", writev(ends[1], two, 2));
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
    /* A trailing slash names a directory and repeated slashes act as one. */
    CHECK("mkdir with a trailing slash", mkdir("slashed/", 0755));
    CHECK("mkdir it again", mkdir("slashed/", 0755));
    CHECK("stat directory with a trailing slash", stat("slashed/", &metadata));
    CHECK("stat file with a trailing slash", stat("moved/", &metadata));
    CHECK("open file with a trailing slash", open("moved/", O_RDONLY));
    CHECK("mkdir with repeated slashes", mkdir("slashed//inner//", 0755));
    CHECK("rmdir with trailing slashes", rmdir("slashed//inner/"));
    CHECK("rmdir with a trailing slash", rmdir("slashed/"));
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

static long long nanoseconds(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return now.tv_sec * 1000000000LL + now.tv_nsec;
}

/* A timed wait or sleep lasts at least its timeout: libc++'s condition_variable::wait_for reports an early return as
 * no timeout, so a caller waiting for the timeout never gets it. Fractions of a millisecond expose rounding. */
static void timed_waits(void)
{
    pthread_mutex_t mutex = PTHREAD_MUTEX_INITIALIZER;
    pthread_condattr_t attributes;
    pthread_cond_t condition;
    pthread_condattr_init(&attributes);
    pthread_condattr_setclock(&attributes, CLOCK_MONOTONIC);
    pthread_cond_init(&condition, &attributes);
    int early_waits = 0, early_sleeps = 0, timeouts = 0;
    pthread_mutex_lock(&mutex);
    for (int round = 0; round < 10; round++) {
        long long deadline = nanoseconds() + 7300000 + round * 100000;
        struct timespec until = {deadline / 1000000000, deadline % 1000000000};
        if (pthread_cond_timedwait(&condition, &mutex, &until) == ETIMEDOUT) timeouts++;
        if (nanoseconds() < deadline) early_waits++;
        long long start = nanoseconds();
        struct timespec pause = {0, 1300000 + round * 100000};
        nanosleep(&pause, 0);
        if (nanoseconds() - start < pause.tv_nsec) early_sleeps++;
    }
    pthread_mutex_unlock(&mutex);
    printf("%-44s %d of 10\n", "timed condition waits that time out", timeouts);
    FACT("no timed condition wait ends early", early_waits == 0);
    FACT("no nanosleep ends early", early_sleeps == 0);
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
    timed_waits();
    if (access("/usr/share/zoneinfo/Europe/Berlin", F_OK) == 0) {
        setenv("TZ", "Europe/Berlin", 1);
        tzset();
        moment = 1690000000;
        strftime(text, sizeof text, "%Y-%m-%d %H:%M:%S %Z", localtime(&moment));
        printf("%-44s %s\n", "localtime with zoneinfo", text);
    }
}

/* Dynamic loading of a library nobody provides: natively the loader finds no file, in portable mode no native extension
 * serves the name (tests/extensions.sh covers the libraries one does). The messages differ; both name what failed. */
static void loading(void)
{
    FACT("dlopen of a missing library fails", dlopen("libllvmnet-no-such-library.so.1", RTLD_NOW) == 0);
    const char *error = dlerror();
    FACT("dlerror names the library", error && strstr(error, "libllvmnet-no-such-library.so.1"));
    FACT("dlerror is cleared by reading it", dlerror() == 0);
    FACT("dlopen by path, RTLD_LAZY | RTLD_GLOBAL", dlopen("/nonexistent/libllvmnet-no-such-library.so.1", RTLD_LAZY | RTLD_GLOBAL) == 0);
    error = dlerror();
    FACT("dlerror names the path", error && strstr(error, "/nonexistent/libllvmnet-no-such-library.so.1"));
    FACT("dlsym RTLD_DEFAULT of a missing symbol", dlsym(RTLD_DEFAULT, "llvmnet_no_such_symbol") == 0);
    error = dlerror();
    FACT("dlerror names the symbol", error && strstr(error, "llvmnet_no_such_symbol"));
    FACT("dlerror without an error", dlerror() == 0);
}

int main(void)
{
    setvbuf(stdout, 0, _IONBF, 0);
    files();
    pipes();
    vectors();
    filesystem();
    memory();
    process();
    clocks();
    loading();
    return 0;
}
