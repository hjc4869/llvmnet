#ifndef _LLVMNET_SETJMP_H
#define _LLVMNET_SETJMP_H
#ifdef __cplusplus
extern "C" {
#endif
typedef struct { unsigned long long __storage[18]; } jmp_buf[1];
typedef jmp_buf sigjmp_buf;
int setjmp(jmp_buf) __attribute__((returns_twice));
int _setjmp(jmp_buf) __attribute__((returns_twice));
int sigsetjmp(sigjmp_buf, int) __attribute__((returns_twice));
int __sigsetjmp(sigjmp_buf, int) __attribute__((returns_twice));
void longjmp(jmp_buf, int) __attribute__((noreturn));
void _longjmp(jmp_buf, int) __attribute__((noreturn));
void siglongjmp(sigjmp_buf, int) __attribute__((noreturn));
#ifdef __cplusplus
}
#endif
#endif