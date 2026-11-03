/* The synthetic API of tests/ext-structs.sh (NX-07): structures passed by value and returned, in the shapes whose
 * passing differs between the hosts' calling conventions. A pair of floats is a homogeneous floating-point aggregate on
 * ARM64 and one SSE register on x86-64 SysV; an int with a double takes an integer and an SSE register on SysV and goes
 * by reference on Windows; 17 bytes go in memory or by reference everywhere; four doubles are a homogeneous aggregate in
 * four registers on ARM64 and memory elsewhere. */
#ifndef LLVMNET_STRUCTS_H
#define LLVMNET_STRUCTS_H

#include <stdint.h>

#if defined(_WIN32) && defined(LLVMNET_STRUCTS_BUILD)
#define STRUCTS_API __declspec(dllexport)
#elif defined(_WIN32)
#define STRUCTS_API __declspec(dllimport)
#else
#define STRUCTS_API
#endif

typedef struct { float x, y; } pairf;
typedef struct { int32_t i; double d; } mixed;
typedef struct { uint8_t bytes[17]; } odd17;
typedef struct { double a, b, c, d; } quad;
typedef struct tagged { pairf point; int32_t tag; } tagged;
typedef struct { int16_t s; uint8_t c; } small3;

STRUCTS_API pairf pair_scale(pairf p, float factor);
STRUCTS_API float pair_length2(pairf p);
STRUCTS_API mixed mixed_make(int32_t i, double d);
STRUCTS_API double mixed_sum(mixed m, int32_t scale);
STRUCTS_API odd17 odd_reverse(odd17 v);
STRUCTS_API uint32_t odd_sum(odd17 v);
STRUCTS_API quad quad_add(quad a, quad b);
STRUCTS_API double quad_dot(quad a, quad b);
STRUCTS_API tagged tagged_make(pairf point, int32_t tag);
STRUCTS_API small3 small_next(small3 v);
STRUCTS_API int32_t structs_version(void);

#endif
