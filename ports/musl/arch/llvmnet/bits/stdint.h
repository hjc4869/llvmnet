/* llvmnet port of musl: the fast 16- and 32-bit types are 64-bit, as in glibc on x86-64 and in the original
 * dotnet64-v1 headers, so programs whose results depend on their width (flang-rt's random numbers, for
 * example) behave as native glibc builds do. musl itself does not use these types. */
typedef int64_t int_fast16_t;
typedef int64_t int_fast32_t;
typedef uint64_t uint_fast16_t;
typedef uint64_t uint_fast32_t;

#define INT_FAST16_MIN  INT64_MIN
#define INT_FAST32_MIN  INT64_MIN

#define INT_FAST16_MAX  INT64_MAX
#define INT_FAST32_MAX  INT64_MAX

#define UINT_FAST16_MAX UINT64_MAX
#define UINT_FAST32_MAX UINT64_MAX

#define INTPTR_MIN      INT64_MIN
#define INTPTR_MAX      INT64_MAX
#define UINTPTR_MAX     UINT64_MAX
#define PTRDIFF_MIN     INT64_MIN
#define PTRDIFF_MAX     INT64_MAX
#define SIZE_MAX        UINT64_MAX
