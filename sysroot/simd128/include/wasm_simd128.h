#ifndef __LLVMNET_WASM_SIMD128_H
#define __LLVMNET_WASM_SIMD128_H
#ifndef __LLVMNET_SIMD128__
#error "The llvmnet SIMD128 compatibility header requires --simd128 (--isa=wasm-simd128)"
#endif
#if __SIZEOF_POINTER__ != 8
#error "The llvmnet SIMD128 compatibility header requires a 64-bit pointer ABI"
#endif
/* The WebAssembly SIMD128 subset llvmnet supports, written as Clang's own wasm_simd128.h is: Clang vector extensions,
 * which the typed vector lowering (ADR-16 Level 1) translates to Vector128 code, and for the operations that header
 * takes from llvm.wasm.* intrinsics, calls of __llvmnet_simd128_* helpers in LlvmNet.Runtime (Simd128.cs). */
#include <stdint.h>
typedef uint8_t v128_t __attribute__((vector_size(16)));
typedef int8_t __llvmnet_i8x16 __attribute__((vector_size(16)));
typedef uint8_t __llvmnet_u8x16 __attribute__((vector_size(16)));
typedef int16_t __llvmnet_i16x8 __attribute__((vector_size(16)));
typedef uint16_t __llvmnet_u16x8 __attribute__((vector_size(16)));
typedef int32_t __llvmnet_i32x4 __attribute__((vector_size(16)));
typedef uint32_t __llvmnet_u32x4 __attribute__((vector_size(16)));
typedef int64_t __llvmnet_i64x2 __attribute__((vector_size(16)));
typedef float __llvmnet_f32x4 __attribute__((vector_size(16)));
typedef int8_t __llvmnet_i8x8 __attribute__((vector_size(8)));
typedef uint8_t __llvmnet_u8x8 __attribute__((vector_size(8)));
typedef int16_t __llvmnet_i16x4 __attribute__((vector_size(8)));
typedef v128_t __llvmnet_v128_u __attribute__((__aligned__(1), __may_alias__));
typedef int64_t __llvmnet_i64_u __attribute__((__aligned__(1), __may_alias__));
#ifdef __cplusplus
extern "C" {
#endif
#define __LLVMNET_SIMD_PURE(name) __asm__("__llvmnet_simd128_" name) __attribute__((const))
v128_t __llvmnet_swizzle(v128_t, v128_t) __LLVMNET_SIMD_PURE("swizzle");
v128_t __llvmnet_bitselect(v128_t, v128_t, v128_t) __LLVMNET_SIMD_PURE("bitselect");
int __llvmnet_bitmask_i8(v128_t) __LLVMNET_SIMD_PURE("bitmask_i8");
int __llvmnet_alltrue_i8(v128_t) __LLVMNET_SIMD_PURE("alltrue_i8");
int __llvmnet_anytrue(v128_t) __LLVMNET_SIMD_PURE("anytrue");
v128_t __llvmnet_narrow_i32(v128_t, v128_t) __LLVMNET_SIMD_PURE("narrow_i32");
v128_t __llvmnet_narrow_i16(v128_t, v128_t) __LLVMNET_SIMD_PURE("narrow_i16");
v128_t __llvmnet_narrow_u16(v128_t, v128_t) __LLVMNET_SIMD_PURE("narrow_u16");
v128_t __llvmnet_q15mulr(v128_t, v128_t) __LLVMNET_SIMD_PURE("q15mulr");
v128_t __llvmnet_pmin_f32(v128_t, v128_t) __LLVMNET_SIMD_PURE("pmin_f32");
v128_t __llvmnet_trunc_sat_i32(v128_t) __LLVMNET_SIMD_PURE("trunc_sat_i32");
v128_t __llvmnet_trunc_sat_u32(v128_t) __LLVMNET_SIMD_PURE("trunc_sat_u32");
#undef __LLVMNET_SIMD_PURE
#ifdef __cplusplus
}
#endif

#define __LLVMNET_SIMD static __inline__ __attribute__((__always_inline__, __nodebug__))

__LLVMNET_SIMD v128_t wasm_v128_load(const void *address) { return *(const __llvmnet_v128_u *)address; }
__LLVMNET_SIMD void wasm_v128_store(void *address, v128_t value) { *(__llvmnet_v128_u *)address = value; }
__LLVMNET_SIMD v128_t wasm_v128_load64_zero(const void *address) { return (v128_t)(__llvmnet_i64x2){*(const __llvmnet_i64_u *)address, 0}; }
__LLVMNET_SIMD v128_t wasm_i8x16_splat(int8_t value) { return (v128_t)((__llvmnet_i8x16){0} + value); }
__LLVMNET_SIMD v128_t wasm_i16x8_splat(int16_t value) { return (v128_t)((__llvmnet_i16x8){0} + value); }
__LLVMNET_SIMD v128_t wasm_i32x4_splat(int32_t value) { return (v128_t)((__llvmnet_i32x4){0} + value); }
__LLVMNET_SIMD v128_t wasm_i8x16_add(v128_t left, v128_t right) { return (v128_t)((__llvmnet_u8x16)left + (__llvmnet_u8x16)right); }
__LLVMNET_SIMD v128_t wasm_i8x16_sub(v128_t left, v128_t right) { return (v128_t)((__llvmnet_u8x16)left - (__llvmnet_u8x16)right); }
__LLVMNET_SIMD v128_t wasm_i32x4_add(v128_t left, v128_t right) { return (v128_t)((__llvmnet_u32x4)left + (__llvmnet_u32x4)right); }
__LLVMNET_SIMD v128_t wasm_i32x4_sub(v128_t left, v128_t right) { return (v128_t)((__llvmnet_u32x4)left - (__llvmnet_u32x4)right); }
__LLVMNET_SIMD v128_t wasm_i16x8_mul(v128_t left, v128_t right) { return (v128_t)((__llvmnet_u16x8)left * (__llvmnet_u16x8)right); }
/* WebAssembly shifts take the count modulo the lane width. */
__LLVMNET_SIMD v128_t wasm_i8x16_shl(v128_t value, uint32_t count) { return (v128_t)((__llvmnet_u8x16)value << (count & 7)); }
__LLVMNET_SIMD v128_t wasm_u8x16_shr(v128_t value, uint32_t count) { return (v128_t)((__llvmnet_u8x16)value >> (count & 7)); }
__LLVMNET_SIMD v128_t wasm_i16x8_shr(v128_t value, uint32_t count) { return (v128_t)((__llvmnet_i16x8)value >> (count & 15)); }
__LLVMNET_SIMD v128_t wasm_i32x4_shr(v128_t value, uint32_t count) { return (v128_t)((__llvmnet_i32x4)value >> (count & 31)); }
__LLVMNET_SIMD v128_t wasm_v128_or(v128_t left, v128_t right) { return left | right; }
__LLVMNET_SIMD v128_t wasm_i8x16_swizzle(v128_t value, v128_t indices) { return __llvmnet_swizzle(value, indices); }
__LLVMNET_SIMD v128_t wasm_v128_bitselect(v128_t left, v128_t right, v128_t mask) { return __llvmnet_bitselect(left, right, mask); }
__LLVMNET_SIMD int wasm_i8x16_bitmask(v128_t value) { return __llvmnet_bitmask_i8(value); }
__LLVMNET_SIMD int wasm_i8x16_all_true(v128_t value) { return __llvmnet_alltrue_i8(value); }
__LLVMNET_SIMD int wasm_v128_any_true(v128_t value) { return __llvmnet_anytrue(value); }
__LLVMNET_SIMD v128_t wasm_u8x16_gt(v128_t left, v128_t right) { return (v128_t)((__llvmnet_u8x16)left > (__llvmnet_u8x16)right); }
__LLVMNET_SIMD v128_t wasm_u8x16_lt(v128_t left, v128_t right) { return (v128_t)((__llvmnet_u8x16)left < (__llvmnet_u8x16)right); }
__LLVMNET_SIMD v128_t wasm_i16x8_extend_low_i8x16(v128_t value)
{
    return (v128_t)__builtin_convertvector((__llvmnet_i8x8)__builtin_shufflevector((__llvmnet_i8x16)value, (__llvmnet_i8x16)value, 0, 1, 2, 3, 4, 5, 6, 7), __llvmnet_i16x8);
}
__LLVMNET_SIMD v128_t wasm_i16x8_extend_high_i8x16(v128_t value)
{
    return (v128_t)__builtin_convertvector((__llvmnet_i8x8)__builtin_shufflevector((__llvmnet_i8x16)value, (__llvmnet_i8x16)value, 8, 9, 10, 11, 12, 13, 14, 15), __llvmnet_i16x8);
}
__LLVMNET_SIMD v128_t wasm_u16x8_extend_low_u8x16(v128_t value)
{
    return (v128_t)__builtin_convertvector((__llvmnet_u8x8)__builtin_shufflevector((__llvmnet_u8x16)value, (__llvmnet_u8x16)value, 0, 1, 2, 3, 4, 5, 6, 7), __llvmnet_u16x8);
}
__LLVMNET_SIMD v128_t wasm_u16x8_extend_high_u8x16(v128_t value)
{
    return (v128_t)__builtin_convertvector((__llvmnet_u8x8)__builtin_shufflevector((__llvmnet_u8x16)value, (__llvmnet_u8x16)value, 8, 9, 10, 11, 12, 13, 14, 15), __llvmnet_u16x8);
}
__LLVMNET_SIMD v128_t wasm_i32x4_extend_low_i16x8(v128_t value)
{
    return (v128_t)__builtin_convertvector((__llvmnet_i16x4)__builtin_shufflevector((__llvmnet_i16x8)value, (__llvmnet_i16x8)value, 0, 1, 2, 3), __llvmnet_i32x4);
}
__LLVMNET_SIMD v128_t wasm_i32x4_extmul_low_i16x8(v128_t left, v128_t right)
{
    return (v128_t)((__llvmnet_i32x4)wasm_i32x4_extend_low_i16x8(left) * (__llvmnet_i32x4)wasm_i32x4_extend_low_i16x8(right));
}
__LLVMNET_SIMD v128_t wasm_i32x4_extmul_high_i16x8(v128_t left, v128_t right)
{
    __llvmnet_i32x4 high_left = __builtin_convertvector((__llvmnet_i16x4)__builtin_shufflevector((__llvmnet_i16x8)left, (__llvmnet_i16x8)left, 4, 5, 6, 7), __llvmnet_i32x4);
    __llvmnet_i32x4 high_right = __builtin_convertvector((__llvmnet_i16x4)__builtin_shufflevector((__llvmnet_i16x8)right, (__llvmnet_i16x8)right, 4, 5, 6, 7), __llvmnet_i32x4);
    return (v128_t)(high_left * high_right);
}
__LLVMNET_SIMD v128_t wasm_i16x8_add_sat(v128_t left, v128_t right) { return (v128_t)__builtin_elementwise_add_sat((__llvmnet_i16x8)left, (__llvmnet_i16x8)right); }
__LLVMNET_SIMD v128_t wasm_u8x16_add_sat(v128_t left, v128_t right) { return (v128_t)__builtin_elementwise_add_sat((__llvmnet_u8x16)left, (__llvmnet_u8x16)right); }
__LLVMNET_SIMD v128_t wasm_i16x8_narrow_i32x4(v128_t lower, v128_t upper) { return __llvmnet_narrow_i32(lower, upper); }
__LLVMNET_SIMD v128_t wasm_i8x16_narrow_i16x8(v128_t lower, v128_t upper) { return __llvmnet_narrow_i16(lower, upper); }
__LLVMNET_SIMD v128_t wasm_u8x16_narrow_i16x8(v128_t lower, v128_t upper) { return __llvmnet_narrow_u16(lower, upper); }
__LLVMNET_SIMD v128_t wasm_i16x8_q15mulr_sat(v128_t left, v128_t right) { return __llvmnet_q15mulr(left, right); }
/* f32x4.min and max propagate NaN and order -0 below +0: llvm.minimum and maximum. */
__LLVMNET_SIMD v128_t wasm_f32x4_min(v128_t left, v128_t right) { return (v128_t)__builtin_elementwise_minimum((__llvmnet_f32x4)left, (__llvmnet_f32x4)right); }
__LLVMNET_SIMD v128_t wasm_f32x4_max(v128_t left, v128_t right) { return (v128_t)__builtin_elementwise_maximum((__llvmnet_f32x4)left, (__llvmnet_f32x4)right); }
__LLVMNET_SIMD v128_t wasm_f32x4_pmin(v128_t left, v128_t right) { return __llvmnet_pmin_f32(left, right); }
__LLVMNET_SIMD v128_t wasm_f32x4_eq(v128_t left, v128_t right) { return (v128_t)((__llvmnet_f32x4)left == (__llvmnet_f32x4)right); }
__LLVMNET_SIMD v128_t wasm_i32x4_trunc_sat_f32x4(v128_t value) { return __llvmnet_trunc_sat_i32(value); }
__LLVMNET_SIMD v128_t wasm_u32x4_trunc_sat_f32x4(v128_t value) { return __llvmnet_trunc_sat_u32(value); }
#undef __LLVMNET_SIMD

#define wasm_i8x16_const_splat(value) wasm_i8x16_splat(value)
#define wasm_i16x8_const_splat(value) wasm_i16x8_splat(value)
#define __LLVMNET_LANE(lane, count) ((void)sizeof(char[(__builtin_constant_p(lane) && (lane) >= 0 && (lane) < (count)) ? 1 : -1]))
#define wasm_v128_load64_lane(address, value, lane) (__LLVMNET_LANE(lane, 2), __extension__({ \
    __llvmnet_i64x2 __llvmnet_vector = (__llvmnet_i64x2)(value); \
    __llvmnet_vector[lane] = *(const __llvmnet_i64_u *)(address); \
    (v128_t)__llvmnet_vector; }))
#define wasm_v128_store64_lane(address, value, lane) \
    (__LLVMNET_LANE(lane, 2), (void)(*(__llvmnet_i64_u *)(address) = ((__llvmnet_i64x2)(value))[lane]))
#define wasm_u8x16_make(lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7, lane8, lane9, lane10, lane11, lane12, lane13, lane14, lane15) \
    ((v128_t)(__llvmnet_u8x16){(uint8_t)(lane0), (uint8_t)(lane1), (uint8_t)(lane2), (uint8_t)(lane3), (uint8_t)(lane4), (uint8_t)(lane5), \
        (uint8_t)(lane6), (uint8_t)(lane7), (uint8_t)(lane8), (uint8_t)(lane9), (uint8_t)(lane10), (uint8_t)(lane11), (uint8_t)(lane12), \
        (uint8_t)(lane13), (uint8_t)(lane14), (uint8_t)(lane15)})
#define wasm_i8x16_shuffle(left, right, lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7, lane8, lane9, lane10, lane11, lane12, lane13, lane14, lane15) \
    ((v128_t)__builtin_shufflevector((__llvmnet_u8x16)(left), (__llvmnet_u8x16)(right), lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7, \
        lane8, lane9, lane10, lane11, lane12, lane13, lane14, lane15))
#define wasm_i16x8_shuffle(left, right, lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7) \
    ((v128_t)__builtin_shufflevector((__llvmnet_i16x8)(left), (__llvmnet_i16x8)(right), lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7))
#define wasm_i32x4_shuffle(left, right, lane0, lane1, lane2, lane3) \
    ((v128_t)__builtin_shufflevector((__llvmnet_i32x4)(left), (__llvmnet_i32x4)(right), lane0, lane1, lane2, lane3))
#define wasm_i64x2_shuffle(left, right, lane0, lane1) \
    ((v128_t)__builtin_shufflevector((__llvmnet_i64x2)(left), (__llvmnet_i64x2)(right), lane0, lane1))
#endif
