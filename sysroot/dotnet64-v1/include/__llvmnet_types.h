#ifndef __LLVMNET_TYPES_H
#define __LLVMNET_TYPES_H
#define __LLVMNET_ABI_VERSION 1
typedef __SIZE_TYPE__ size_t;
typedef __PTRDIFF_TYPE__ ptrdiff_t;
typedef __INTPTR_TYPE__ intptr_t;
typedef __UINTPTR_TYPE__ uintptr_t;
typedef signed char int8_t;
typedef unsigned char uint8_t;
typedef short int16_t;
typedef unsigned short uint16_t;
typedef int int32_t;
typedef unsigned int uint32_t;
typedef long int64_t;
typedef unsigned long uint64_t;
typedef long intmax_t;
typedef unsigned long uintmax_t;
typedef long ssize_t;
typedef long off_t;
typedef long off64_t;
typedef long time_t;
typedef long clock_t;
typedef int pid_t;
typedef unsigned int mode_t;
typedef unsigned int uid_t;
typedef unsigned int gid_t;
#ifndef __cplusplus
typedef int wchar_t;
#endif
typedef unsigned int wint_t;
typedef struct __llvmnet_file FILE;
#ifdef __cplusplus
#define __LLVMNET_BEGIN_C extern "C" {
#define __LLVMNET_END_C }
#else
#define __LLVMNET_BEGIN_C
#define __LLVMNET_END_C
#endif
#endif