#ifndef __LLVMNET_UTIME_H
#define __LLVMNET_UTIME_H
#include "__llvmnet_types.h"
struct utimbuf { time_t actime; time_t modtime; };
__LLVMNET_BEGIN_C
int utime(const char *, const struct utimbuf *);
__LLVMNET_END_C
#endif