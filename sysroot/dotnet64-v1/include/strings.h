#ifndef __LLVMNET_STRINGS_H
#define __LLVMNET_STRINGS_H
#include <stddef.h>
__LLVMNET_BEGIN_C
int strcasecmp(const char *, const char *);
int strncasecmp(const char *, const char *, size_t);
int bcmp(const void *, const void *, size_t);
__LLVMNET_END_C
#endif