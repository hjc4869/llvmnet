#ifndef __LLVMNET_STDLIB_H
#define __LLVMNET_STDLIB_H
#include <stddef.h>
#define EXIT_SUCCESS 0
#define EXIT_FAILURE 1
#define RAND_MAX 2147483647
#define MB_CUR_MAX 4
typedef struct { int quot; int rem; } div_t;
typedef struct { long quot; long rem; } ldiv_t;
typedef struct { long long quot; long long rem; } lldiv_t;
__LLVMNET_BEGIN_C
static inline div_t div(int numerator, int denominator) { div_t result = {numerator / denominator, numerator % denominator}; return result; }
static inline ldiv_t ldiv(long numerator, long denominator) { ldiv_t result = {numerator / denominator, numerator % denominator}; return result; }
static inline lldiv_t lldiv(long long numerator, long long denominator) { lldiv_t result = {numerator / denominator, numerator % denominator}; return result; }
void *malloc(size_t);
void *calloc(size_t, size_t);
void *realloc(void *, size_t);
void free(void *);
int posix_memalign(void **, size_t, size_t);
void *aligned_alloc(size_t, size_t);
void abort(void) __attribute__((noreturn));
void exit(int) __attribute__((noreturn));
void _Exit(int) __attribute__((noreturn));
int atexit(void (*)(void));
int atoi(const char *);
long atol(const char *);
long long atoll(const char *);
double atof(const char *);
long strtol(const char *, char **, int);
unsigned long strtoul(const char *, char **, int);
long long strtoll(const char *, char **, int);
unsigned long long strtoull(const char *, char **, int);
float strtof(const char *, char **);
double strtod(const char *, char **);
char *getenv(const char *);
int setenv(const char *, const char *, int);
int unsetenv(const char *);
void qsort(void *, size_t, size_t, int (*)(const void *, const void *));
void *bsearch(const void *, const void *, size_t, size_t, int (*)(const void *, const void *));
int abs(int);
long labs(long);
long long llabs(long long);
int rand(void);
void srand(unsigned);
__LLVMNET_END_C
#endif