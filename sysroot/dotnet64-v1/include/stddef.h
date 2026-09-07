#ifndef __LLVMNET_STDDEF_H
#define __LLVMNET_STDDEF_H
#include <__llvmnet_types.h>
#ifdef __cplusplus
#define NULL __null
#else
#define NULL ((void *)0)
#endif
#define offsetof(type, member) __builtin_offsetof(type, member)
typedef struct __attribute__((aligned(16))) { long long __integer; double __floating; } max_align_t;
#endif