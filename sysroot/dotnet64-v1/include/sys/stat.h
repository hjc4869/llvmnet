#ifndef __LLVMNET_SYS_STAT_H
#define __LLVMNET_SYS_STAT_H
#include "../__llvmnet_types.h"
#include <time.h>
struct stat {
    unsigned long st_dev, st_ino, st_nlink;
    unsigned st_mode, st_uid, st_gid;
    int __padding;
    unsigned long st_rdev;
    long st_size, st_blksize, st_blocks;
    struct timespec st_atim, st_mtim, st_ctim;
    long __reserved[3];
};
#define st_atime st_atim.tv_sec
#define st_mtime st_mtim.tv_sec
#define st_ctime st_ctim.tv_sec
#define S_IFMT 0170000
#define S_IFREG 0100000
#define S_IFDIR 0040000
#define S_IFCHR 0020000
#define S_IFLNK 0120000
#define S_IRUSR 0400
#define S_IWUSR 0200
#define S_IXUSR 0100
#define S_IRGRP 0040
#define S_IWGRP 0020
#define S_IXGRP 0010
#define S_IROTH 0004
#define S_IWOTH 0002
#define S_IXOTH 0001
#define S_IRWXU 0700
#define S_IRWXG 0070
#define S_IRWXO 0007
#define S_ISREG(mode) (((mode) & S_IFMT) == S_IFREG)
#define S_ISDIR(mode) (((mode) & S_IFMT) == S_IFDIR)
#define S_ISCHR(mode) (((mode) & S_IFMT) == S_IFCHR)
#define S_ISLNK(mode) (((mode) & S_IFMT) == S_IFLNK)
__LLVMNET_BEGIN_C
int stat(const char *, struct stat *);
int fstat(int, struct stat *);
int lstat(const char *, struct stat *);
int chmod(const char *, mode_t);
int fchmod(int, mode_t);
int mkdir(const char *, mode_t);
__LLVMNET_END_C
#endif