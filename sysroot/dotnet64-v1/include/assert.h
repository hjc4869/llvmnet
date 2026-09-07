#undef assert
#ifdef NDEBUG
#define assert(expression) ((void)0)
#else
#include <__llvmnet_types.h>
__LLVMNET_BEGIN_C
void __llvmnet_assert_fail(const char *, const char *, unsigned, const char *) __attribute__((noreturn));
__LLVMNET_END_C
#define assert(expression) ((expression) ? (void)0 : __llvmnet_assert_fail(#expression, __FILE__, __LINE__, __func__))
#endif
#if !defined(__cplusplus) && __STDC_VERSION__ >= 201112L
#define static_assert _Static_assert
#endif