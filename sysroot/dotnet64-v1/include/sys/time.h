#ifndef __LLVMNET_SYS_TIME_H
#define __LLVMNET_SYS_TIME_H
#include <time.h>
struct timeval { time_t tv_sec; long tv_usec; };
struct timezone { int tz_minuteswest; int tz_dsttime; };
__LLVMNET_BEGIN_C
int gettimeofday(struct timeval *, void *);
__LLVMNET_END_C
#endif