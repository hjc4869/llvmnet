#ifndef __LLVMNET_MATH_H
#define __LLVMNET_MATH_H
#include "__llvmnet_types.h"
#define HUGE_VAL (__builtin_huge_val())
#define HUGE_VALF (__builtin_huge_valf())
#define HUGE_VALL (__builtin_huge_vall())
#define INFINITY (__builtin_inff())
#define NAN (__builtin_nanf(""))
#define FP_INFINITE 1
#define FP_NAN 0
#define FP_NORMAL 4
#define FP_SUBNORMAL 3
#define FP_ZERO 2
#define isfinite(value) __builtin_isfinite(value)
#define isinf(value) __builtin_isinf(value)
#define isnan(value) __builtin_isnan(value)
#define isnormal(value) __builtin_isnormal(value)
#define signbit(value) __builtin_signbit(value)
#define fpclassify(value) __builtin_fpclassify(FP_NAN, FP_INFINITE, FP_NORMAL, FP_SUBNORMAL, FP_ZERO, value)
#define isgreater(left, right) __builtin_isgreater(left, right)
#define isgreaterequal(left, right) __builtin_isgreaterequal(left, right)
#define isless(left, right) __builtin_isless(left, right)
#define islessequal(left, right) __builtin_islessequal(left, right)
#define islessgreater(left, right) __builtin_islessgreater(left, right)
#define isunordered(left, right) __builtin_isunordered(left, right)
#define M_PI 3.14159265358979323846
#define M_PI_2 1.57079632679489661923
#define M_PI_4 0.78539816339744830962
#define M_E 2.71828182845904523536
#define M_LN2 0.69314718055994530942
#define M_SQRT2 1.41421356237309504880
__LLVMNET_BEGIN_C
#define __LLVMNET_MATH_UNARY(name) double name(double); float name##f(float)
__LLVMNET_MATH_UNARY(sqrt);
__LLVMNET_MATH_UNARY(cbrt);
__LLVMNET_MATH_UNARY(sin);
__LLVMNET_MATH_UNARY(cos);
__LLVMNET_MATH_UNARY(tan);
__LLVMNET_MATH_UNARY(asin);
__LLVMNET_MATH_UNARY(acos);
__LLVMNET_MATH_UNARY(atan);
__LLVMNET_MATH_UNARY(sinh);
__LLVMNET_MATH_UNARY(cosh);
__LLVMNET_MATH_UNARY(tanh);
__LLVMNET_MATH_UNARY(atanh);
__LLVMNET_MATH_UNARY(exp);
__LLVMNET_MATH_UNARY(exp2);
__LLVMNET_MATH_UNARY(log);
__LLVMNET_MATH_UNARY(log2);
__LLVMNET_MATH_UNARY(log10);
__LLVMNET_MATH_UNARY(fabs);
__LLVMNET_MATH_UNARY(floor);
__LLVMNET_MATH_UNARY(ceil);
__LLVMNET_MATH_UNARY(trunc);
__LLVMNET_MATH_UNARY(round);
__LLVMNET_MATH_UNARY(rint);
__LLVMNET_MATH_UNARY(erf);
__LLVMNET_MATH_UNARY(tgamma);
#undef __LLVMNET_MATH_UNARY
double pow(double, double);
float powf(float, float);
double atan2(double, double);
float atan2f(float, float);
double hypot(double, double);
float hypotf(float, float);
double copysign(double, double);
float copysignf(float, float);
double fmod(double, double);
float fmodf(float, float);
double modf(double, double *);
float modff(float, float *);
double fmin(double, double);
float fminf(float, float);
double fmax(double, double);
float fmaxf(float, float);
double j1(double);
double fma(double, double, double);
float fmaf(float, float, float);
double ldexp(double, int);
float ldexpf(float, int);
double scalbn(double, int);
float scalbnf(float, int);
double frexp(double, int *);
long lrint(double);
long lrintf(float);
long long llrint(double);
long long llrintf(float);
long lround(double);
long lroundf(float);
__LLVMNET_END_C
#endif