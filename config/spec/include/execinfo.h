/* glibc's backtrace interface, which musl does not have. 723.llvm_r and 823.llvm_s include this header because
   their prebuilt config.h sets HAVE_BACKTRACE, but call these functions only when ENABLE_BACKTRACES is 1, and it is 0.
   Declarations only: a call would fail to link. */
#ifndef LLVMNET_SPEC_EXECINFO_H
#define LLVMNET_SPEC_EXECINFO_H
#ifdef __cplusplus
extern "C" {
#endif
int backtrace(void **buffer, int size);
char **backtrace_symbols(void *const *buffer, int size);
void backtrace_symbols_fd(void *const *buffer, int size, int fd);
#ifdef __cplusplus
}
#endif
#endif
