#ifndef __LLVMNET_SIGNAL_H
#define __LLVMNET_SIGNAL_H
#include <__llvmnet_types.h>
typedef int sig_atomic_t;
typedef void (*__llvmnet_sighandler_t)(int);
#define SIG_DFL ((__llvmnet_sighandler_t)0)
#define SIG_IGN ((__llvmnet_sighandler_t)1)
#define SIG_ERR ((__llvmnet_sighandler_t)-1)
#define SIGHUP 1
#define SIGINT 2
#define SIGQUIT 3
#define SIGILL 4
#define SIGABRT 6
#define SIGFPE 8
#define SIGKILL 9
#define SIGUSR1 10
#define SIGSEGV 11
#define SIGUSR2 12
#define SIGPIPE 13
#define SIGALRM 14
#define SIGTERM 15
__LLVMNET_BEGIN_C
__llvmnet_sighandler_t signal(int, __llvmnet_sighandler_t);
int raise(int);
__LLVMNET_END_C
#endif