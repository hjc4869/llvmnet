#ifndef __LLVMNET_SYS_WAIT_H
#define __LLVMNET_SYS_WAIT_H
#include "../__llvmnet_types.h"
#define WNOHANG 1
#define WUNTRACED 2
#define WCONTINUED 8
#define WEXITSTATUS(status) (((status) >> 8) & 255)
#define WTERMSIG(status) ((status) & 127)
#define WSTOPSIG(status) WEXITSTATUS(status)
#define WIFEXITED(status) (WTERMSIG(status) == 0)
#define WIFSIGNALED(status) (WTERMSIG(status) != 0 && WTERMSIG(status) != 127)
#define WIFSTOPPED(status) (((status) & 255) == 127)
#define WIFCONTINUED(status) ((status) == 65535)
#define WCOREDUMP(status) ((status) & 128)
__LLVMNET_BEGIN_C
pid_t wait(int *);
pid_t waitpid(pid_t, int *, int);
__LLVMNET_END_C
#endif