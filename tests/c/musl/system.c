/* musl over the host system-call layer: files, directories, time, environment, locale, regex, getopt,
 * atexit, setjmp and synchronous signals; compared with native musl 1.2.5. */
#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <getopt.h>
#include <locale.h>
#include <regex.h>
#include <setjmp.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>
#include <wchar.h>
#include <wctype.h>
static jmp_buf jump;
static volatile sig_atomic_t signalled;
static void handler(int signal) { signalled = signal; }
static void at_exit_first(void) { puts("atexit first-registered runs last"); }
static void at_exit_second(void) { puts("atexit second-registered runs first"); }
static int names(const void *a, const void *b) { return strcmp(*(char *const *)a, *(char *const *)b); }
static void deep(int level) { if (level == 0) longjmp(jump, 42); deep(level - 1); }
int main(void)
{
    atexit(at_exit_first); atexit(at_exit_second);
    char directory[] = "musl-system-test";
    mkdir(directory, 0755);
    if (chdir(directory) != 0) return 1;
    int fd = open("data.bin", O_CREAT | O_WRONLY | O_TRUNC, 0644);
    write(fd, "0123456789", 10); close(fd);
    struct stat info; stat("data.bin", &info);
    printf("stat size=%lld regular=%d directory=%d\n", (long long)info.st_size, S_ISREG(info.st_mode), S_ISDIR(info.st_mode));
    fd = open("data.bin", O_RDWR); lseek(fd, 4, SEEK_SET); char bytes[4] = {0}; read(fd, bytes, 3);
    printf("lseek read [%s] ftruncate=%d\n", bytes, ftruncate(fd, 5)); fstat(fd, &info); close(fd);
    printf("after truncate size=%lld\n", (long long)info.st_size);
    mkdir("child", 0755); fclose(fopen("child/a.txt", "w")); fclose(fopen("b.txt", "w"));
    rename("b.txt", "c.txt");
    DIR *dir = opendir("."); struct dirent *entry; char *list[16]; int count = 0;
    while ((entry = readdir(dir)) && count < 16) list[count++] = strdup(entry->d_name);
    closedir(dir); qsort(list, count, sizeof *list, names);
    for (int i = 0; i < count; i++) { printf("entry %s\n", list[i]); free(list[i]); }
    errno = 0; printf("rmdir non-empty=%d errno=%s\n", rmdir("child"), strerror(errno));
    printf("access=%d missing=%d errno=%s\n", access("c.txt", F_OK), access("missing", F_OK), strerror(errno));
    unlink("child/a.txt"); rmdir("child"); unlink("c.txt"); unlink("data.bin");
    chdir(".."); printf("rmdir=%d\n", rmdir(directory));
    time_t epoch = 1700000000; struct tm utc; gmtime_r(&epoch, &utc); char when[64];
    strftime(when, sizeof when, "%Y-%m-%d %H:%M:%S %A %j", &utc);
    printf("gmtime %s mktime-roundtrip=%d\n", when, (int)(timegm(&utc) == epoch));
    struct timespec first, second; clock_gettime(CLOCK_MONOTONIC, &first); clock_gettime(CLOCK_MONOTONIC, &second);
    printf("monotonic ordered=%d realtime-sane=%d\n", second.tv_sec > first.tv_sec || second.tv_sec == first.tv_sec && second.tv_nsec >= first.tv_nsec, time(0) > 1700000000);
    setenv("LLVMNET_MUSL_VAR", "value=1", 1); printf("getenv %s unset=%d %p\n", getenv("LLVMNET_MUSL_VAR"), unsetenv("LLVMNET_MUSL_VAR"), (void *)getenv("LLVMNET_MUSL_VAR"));
    printf("locale %s\n", setlocale(LC_CTYPE, "C.UTF-8"));
    wchar_t wide[16]; size_t converted = mbstowcs(wide, "h\xc3\xa9llo \xe2\x82\xac", 16);
    char narrow[32]; wcstombs(narrow, wide, sizeof narrow);
    printf("mbstowcs %zu %lc upper=%lc alpha=%d width=%d back=%s\n", converted, (wint_t)wide[1], (wint_t)towupper(wide[1]), iswalpha(wide[6]) != 0, wcwidth(L'\x4e00'), narrow);
    regex_t expression; regmatch_t match[3];
    regcomp(&expression, "([a-z]+)-([0-9]+)", REG_EXTENDED);
    if (regexec(&expression, "id: item-204;", 3, match, 0) == 0)
        printf("regex %.*s %.*s\n", (int)(match[1].rm_eo - match[1].rm_so), "id: item-204;" + match[1].rm_so, (int)(match[2].rm_eo - match[2].rm_so), "id: item-204;" + match[2].rm_so);
    regfree(&expression);
    char *arguments[] = { "program", "-v", "-o", "out.txt", "rest", 0 }; int option; optind = 1;
    while ((option = getopt(5, arguments, "vo:")) != -1) printf("getopt %c %s\n", option, option == 'o' ? optarg : "-");
    printf("operand %s\n", arguments[optind]);
    int value = setjmp(jump);
    if (value == 0) deep(20); else printf("longjmp %d\n", value);
    signal(SIGUSR1, handler); raise(SIGUSR1); printf("signal %d\n", signalled);
    srand(12345); int r1 = rand(), r2 = rand(); printf("rand %d %d\n", r1, r2);
    printf("strerror %s | %s | %s\n", strerror(ENOENT), strerror(EACCES), strerror(EINVAL));
    return 0;
}
