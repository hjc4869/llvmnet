#ifndef __LLVMNET_ERRNO_H
#define __LLVMNET_ERRNO_H
#include <__llvmnet_types.h>
__LLVMNET_BEGIN_C
int *__llvmnet_errno_location(void);
__LLVMNET_END_C
#define errno (*__llvmnet_errno_location())
#define EPERM 1
#define ENOENT 2
#define ESRCH 3
#define EINTR 4
#define EIO 5
#define ENXIO 6
#define E2BIG 7
#define EBADF 9
#define ECHILD 10
#define EAGAIN 11
#define EWOULDBLOCK EAGAIN
#define ENOMEM 12
#define EACCES 13
#define EFAULT 14
#define EBUSY 16
#define EEXIST 17
#define EXDEV 18
#define ENODEV 19
#define ENOTDIR 20
#define EISDIR 21
#define EINVAL 22
#define ENFILE 23
#define EMFILE 24
#define ENOTTY 25
#define EFBIG 27
#define ENOSPC 28
#define ESPIPE 29
#define EROFS 30
#define EPIPE 32
#define EDOM 33
#define ERANGE 34
#define EDEADLK 35
#define ENAMETOOLONG 36
#define ENOSYS 38
#define ENOTEMPTY 39
#define EOVERFLOW 75
#define ENOTSUP 95
#define ETIMEDOUT 110
__LLVMNET_BEGIN_C
__LLVMNET_END_C
#endif