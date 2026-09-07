#include <sys/mman.h>
#include <sys/stat.h>
#include <dirent.h>
#include <unistd.h>
#include <fcntl.h>
#include <stdio.h>
#include <signal.h>
#include <string.h>
#include <errno.h>
static volatile sig_atomic_t received;
static void handler(int signal_number) { received = signal_number; }
int main(int argc, char **argv)
{
    if (argc != 2) return 1;
    unsigned char *memory = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_ANONYMOUS | MAP_PRIVATE, -1, 0);
    if (memory == MAP_FAILED || memory[123]) return 2;
    memory[123] = 42;
    if (memory[123] != 42 || munmap(memory, 4096)) return 3;
    if (mkdir(argv[1], 0700)) return 4;
    char path[4096];
    snprintf(path, sizeof(path), "%s/data", argv[1]);
    int descriptor = open(path, O_RDWR | O_CREAT | O_EXCL, 0600);
    if (descriptor < 0 || write(descriptor, "abcd", 4) != 4) return 5;
    memory = mmap(NULL, 4, PROT_READ | PROT_WRITE, MAP_SHARED, descriptor, 0);
    if (memory == MAP_FAILED || memcmp(memory, "abcd", 4)) return 6;
    memory[1] = 'Z';
    if (msync(memory, 4, MS_SYNC) || munmap(memory, 4)) return 7;
    if (lseek(descriptor, 0, SEEK_SET)) return 8;
    char bytes[4];
    if (read(descriptor, bytes, 4) != 4 || memcmp(bytes, "aZcd", 4)) return 9;
    if (fchmod(descriptor, 0640) || close(descriptor)) return 10;
    struct stat metadata;
    if (stat(path, &metadata) || (metadata.st_mode & 0777) != 0640) return 11;
    DIR *directory = opendir(argv[1]);
    if (!directory) return 12;
    struct dirent *entry;
    int found = 0;
    while ((entry = readdir(directory)))
        if (!strcmp(entry->d_name, "data") && entry->d_type == DT_REG) found++;
    if (found != 1 || closedir(directory)) return 13;
    if (unlink(path) || rmdir(argv[1])) return 14;
    if (signal(SIGUSR1, handler) == SIG_ERR || raise(SIGUSR1) || received != SIGUSR1) return 15;
    if (signal(SIGUSR1, SIG_IGN) == SIG_ERR || raise(SIGUSR1)) return 16;
    if (!strstr(strerror(ENOENT), "No such file")) return 17;
    puts("posix: mapping, directories, permissions and signal callback");
    return 0;
}