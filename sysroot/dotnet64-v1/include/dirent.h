#ifndef __LLVMNET_DIRENT_H
#define __LLVMNET_DIRENT_H
#include "__llvmnet_types.h"
typedef struct __llvmnet_dir DIR;
struct dirent {
    unsigned long d_ino;
    long d_off;
    unsigned short d_reclen;
    unsigned char d_type;
    char d_name[256];
};
#define DT_UNKNOWN 0
#define DT_FIFO 1
#define DT_CHR 2
#define DT_DIR 4
#define DT_BLK 6
#define DT_REG 8
#define DT_LNK 10
#define DT_SOCK 12
__LLVMNET_BEGIN_C
DIR *opendir(const char *);
struct dirent *readdir(DIR *);
int closedir(DIR *);
__LLVMNET_END_C
#endif