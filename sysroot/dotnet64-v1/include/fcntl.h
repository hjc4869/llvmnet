#ifndef __LLVMNET_FCNTL_H
#define __LLVMNET_FCNTL_H
#include <__llvmnet_types.h>
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
__LLVMNET_BEGIN_C
int open(const char *, int, ...);
__LLVMNET_END_C
#endif