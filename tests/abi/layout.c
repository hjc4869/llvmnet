/* RL-02: the data layout of dotnet64-v2, printed one fact per line. tests/abi-freeze.sh compiles this in portable mode
 * and compares the output with tests/abi/dotnet64-v2.layout: any difference changes the frozen ABI (ADR-09). */
#include <dirent.h>
#include <errno.h>
#include <float.h>
#include <limits.h>
#include <pthread.h>
#include <setjmp.h>
#include <signal.h>
#include <stdalign.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <sys/types.h>
#include <time.h>
#include <wchar.h>

#define TYPE(t) printf("type %-22s size %2zu align %2zu\n", #t, sizeof(t), alignof(t))
#define FIELD(t, f) printf("field %s.%s offset %zu size %zu\n", #t, #f, offsetof(t, f), sizeof(((t *)0)->f))
#define VALUE(v) printf("value %-16s %lld\n", #v, (long long)(v))

int main(void)
{
    TYPE(char); TYPE(short); TYPE(int); TYPE(long); TYPE(long long); TYPE(__int128);
    TYPE(float); TYPE(double); TYPE(long double); TYPE(void *); TYPE(size_t); TYPE(ptrdiff_t);
    TYPE(wchar_t); TYPE(wint_t); TYPE(int_fast16_t); TYPE(int_fast32_t); TYPE(uint_fast32_t); TYPE(intmax_t);
    TYPE(max_align_t); TYPE(off_t); TYPE(time_t); TYPE(clock_t); TYPE(fpos_t); TYPE(mbstate_t);
    TYPE(va_list); TYPE(jmp_buf); TYPE(sigjmp_buf); TYPE(sigset_t);
    TYPE(struct stat); TYPE(struct timespec); TYPE(struct timeval); TYPE(struct tm); TYPE(struct dirent);
    TYPE(pthread_t); TYPE(pthread_attr_t); TYPE(pthread_mutex_t); TYPE(pthread_mutexattr_t); TYPE(pthread_cond_t);
    TYPE(pthread_rwlock_t); TYPE(pthread_key_t); TYPE(pthread_once_t); TYPE(struct sigaction);
    FIELD(struct stat, st_dev); FIELD(struct stat, st_ino); FIELD(struct stat, st_mode); FIELD(struct stat, st_size);
    FIELD(struct stat, st_mtim); FIELD(struct timespec, tv_nsec); FIELD(struct tm, tm_gmtoff); FIELD(struct tm, tm_zone);
    FIELD(struct dirent, d_name); FIELD(struct sigaction, sa_mask);
    /* The software va_list is SysV's: gp_offset, fp_offset, overflow_arg_area, reg_save_area. */
    VALUE(sizeof(((va_list *)0)[0][0]));
    VALUE(CHAR_MIN); VALUE(WCHAR_MIN); VALUE(LDBL_MANT_DIG); VALUE(LDBL_MAX_EXP); VALUE(LDBL_MIN_EXP); VALUE(FLT_EVAL_METHOD);
    VALUE(BUFSIZ); VALUE(PATH_MAX); VALUE(NAME_MAX); VALUE(FILENAME_MAX); VALUE(EOF); VALUE(RAND_MAX); VALUE(CLOCKS_PER_SEC);
    VALUE(NSIG); VALUE(SIGABRT); VALUE(SIGSEGV); VALUE(SIGUSR1); VALUE(SIGCHLD);
    /* errno values: Linux numbering. */
    VALUE(EPERM); VALUE(ENOENT); VALUE(EINTR); VALUE(EIO); VALUE(EBADF); VALUE(ECHILD); VALUE(EAGAIN); VALUE(ENOMEM);
    VALUE(EACCES); VALUE(EEXIST); VALUE(ENOTDIR); VALUE(EISDIR); VALUE(EINVAL); VALUE(EMFILE); VALUE(ENOSPC); VALUE(ESPIPE);
    VALUE(EPIPE); VALUE(EDOM); VALUE(ERANGE); VALUE(ENAMETOOLONG); VALUE(ENOSYS); VALUE(ENOTEMPTY); VALUE(ELOOP);
    VALUE(EOVERFLOW); VALUE(EOPNOTSUPP); VALUE(ETIMEDOUT); VALUE(EILSEQ);
    return 0;
}
