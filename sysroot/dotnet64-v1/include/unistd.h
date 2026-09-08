#ifndef __LLVMNET_UNISTD_H
#define __LLVMNET_UNISTD_H
#include "__llvmnet_types.h"
#include <stddef.h>
#define STDIN_FILENO 0
#define STDOUT_FILENO 1
#define STDERR_FILENO 2
#define _SC_CLK_TCK 2
#define _SC_PAGESIZE 30
#define _SC_NPROCESSORS_CONF 83
#define _SC_NPROCESSORS_ONLN 84
__LLVMNET_BEGIN_C
ssize_t read(int, void *, size_t);
ssize_t write(int, const void *, size_t);
int close(int);
off_t lseek(int, off_t, int);
int ftruncate(int, off_t);
int dup(int);
char *getcwd(char *, size_t);
int chdir(const char *);
int chown(const char *, uid_t, gid_t);
int fchown(int, uid_t, gid_t);
int unlink(const char *);
int rmdir(const char *);
int isatty(int);
long sysconf(int);
int usleep(unsigned);
int getentropy(void *, size_t);
void _exit(int) __attribute__((noreturn));
__LLVMNET_END_C
#endif