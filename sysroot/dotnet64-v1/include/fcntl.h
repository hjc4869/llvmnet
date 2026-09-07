#ifndef __LLVMNET_FCNTL_H
#define __LLVMNET_FCNTL_H
#include "__llvmnet_types.h"
#define O_RDONLY 0
#define O_WRONLY 1
#define O_RDWR 2
#define O_CREAT 64
#define O_EXCL 128
#define O_TRUNC 512
#define O_APPEND 1024
#define O_NONBLOCK 2048
#define O_BINARY 0
#define O_CLOEXEC 524288
#define F_GETFD 1
#define F_SETFD 2
#define F_GETFL 3
#define F_SETFL 4
#define FD_CLOEXEC 1
__LLVMNET_BEGIN_C
int open(const char *, int, ...);
int fcntl(int, int, ...);
__LLVMNET_END_C
#endif