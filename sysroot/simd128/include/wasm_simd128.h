#ifndef __LLVMNET_WASM_SIMD128_H
#define __LLVMNET_WASM_SIMD128_H
#ifndef __LLVMNET_SIMD128__
#error "The llvmnet SIMD128 compatibility header requires --simd128"
#endif
#if __SIZEOF_POINTER__ != 8
#error "The llvmnet SIMD128 compatibility header requires a 64-bit pointer ABI"
#endif
#include <stdint.h>
typedef uint8_t v128_t __attribute__((vector_size(16)));
#ifdef __cplusplus
extern "C" {
#endif
#define __LLVMNET_SIMD_PURE(name) __asm__("__llvmnet_simd128_" name) __attribute__((const))
v128_t wasm_v128_load(const void *) __asm__("__llvmnet_simd128_load") __attribute__((pure));
void wasm_v128_store(void *, v128_t) __asm__("__llvmnet_simd128_store");
v128_t wasm_v128_load64_zero(const void *) __asm__("__llvmnet_simd128_load64_zero") __attribute__((pure));
v128_t __llvmnet_load64_lane(const void *, v128_t, int) __asm__("__llvmnet_simd128_load64_lane") __attribute__((pure));
void __llvmnet_store64_lane(void *, v128_t, int) __asm__("__llvmnet_simd128_store64_lane");
v128_t __llvmnet_make(int64_t, int64_t) __LLVMNET_SIMD_PURE("make");
v128_t __llvmnet_shuffle(v128_t, v128_t, int64_t, int64_t) __LLVMNET_SIMD_PURE("shuffle");
v128_t wasm_i8x16_splat(int) __LLVMNET_SIMD_PURE("splat_i8");
v128_t wasm_i16x8_splat(int) __LLVMNET_SIMD_PURE("splat_i16");
v128_t wasm_i32x4_splat(int) __LLVMNET_SIMD_PURE("splat_i32");
v128_t wasm_i8x16_add(v128_t, v128_t) __LLVMNET_SIMD_PURE("add_i8");
v128_t wasm_i8x16_sub(v128_t, v128_t) __LLVMNET_SIMD_PURE("sub_i8");
v128_t wasm_i32x4_add(v128_t, v128_t) __LLVMNET_SIMD_PURE("add_i32");
v128_t wasm_i32x4_sub(v128_t, v128_t) __LLVMNET_SIMD_PURE("sub_i32");
v128_t wasm_i16x8_mul(v128_t, v128_t) __LLVMNET_SIMD_PURE("mul_i16");
v128_t wasm_i8x16_shl(v128_t, unsigned) __LLVMNET_SIMD_PURE("shl_i8");
v128_t wasm_u8x16_shr(v128_t, unsigned) __LLVMNET_SIMD_PURE("shr_u8");
v128_t wasm_i16x8_shr(v128_t, unsigned) __LLVMNET_SIMD_PURE("shr_i16");
v128_t wasm_i32x4_shr(v128_t, unsigned) __LLVMNET_SIMD_PURE("shr_i32");
v128_t wasm_v128_or(v128_t, v128_t) __LLVMNET_SIMD_PURE("or");
v128_t wasm_i8x16_swizzle(v128_t, v128_t) __LLVMNET_SIMD_PURE("swizzle");
v128_t wasm_v128_bitselect(v128_t, v128_t, v128_t) __LLVMNET_SIMD_PURE("bitselect");
int wasm_i8x16_bitmask(v128_t) __LLVMNET_SIMD_PURE("bitmask_i8");
int wasm_i8x16_all_true(v128_t) __LLVMNET_SIMD_PURE("alltrue_i8");
int wasm_v128_any_true(v128_t) __LLVMNET_SIMD_PURE("anytrue");
v128_t wasm_u8x16_gt(v128_t, v128_t) __LLVMNET_SIMD_PURE("gt_u8");
v128_t wasm_u8x16_lt(v128_t, v128_t) __LLVMNET_SIMD_PURE("lt_u8");
v128_t wasm_i16x8_extend_low_i8x16(v128_t) __LLVMNET_SIMD_PURE("extend_low_i8");
v128_t wasm_i16x8_extend_high_i8x16(v128_t) __LLVMNET_SIMD_PURE("extend_high_i8");
v128_t wasm_u16x8_extend_low_u8x16(v128_t) __LLVMNET_SIMD_PURE("extend_low_u8");
v128_t wasm_u16x8_extend_high_u8x16(v128_t) __LLVMNET_SIMD_PURE("extend_high_u8");
v128_t wasm_i32x4_extend_low_i16x8(v128_t) __LLVMNET_SIMD_PURE("extend_low_i16");
v128_t wasm_i32x4_extmul_low_i16x8(v128_t, v128_t) __LLVMNET_SIMD_PURE("extmul_low_i16");
v128_t wasm_i32x4_extmul_high_i16x8(v128_t, v128_t) __LLVMNET_SIMD_PURE("extmul_high_i16");
v128_t wasm_i16x8_add_sat(v128_t, v128_t) __LLVMNET_SIMD_PURE("add_sat_i16");
v128_t wasm_u8x16_add_sat(v128_t, v128_t) __LLVMNET_SIMD_PURE("add_sat_u8");
v128_t wasm_i16x8_narrow_i32x4(v128_t, v128_t) __LLVMNET_SIMD_PURE("narrow_i32");
v128_t wasm_i8x16_narrow_i16x8(v128_t, v128_t) __LLVMNET_SIMD_PURE("narrow_i16");
v128_t wasm_u8x16_narrow_i16x8(v128_t, v128_t) __LLVMNET_SIMD_PURE("narrow_u16");
v128_t wasm_i16x8_q15mulr_sat(v128_t, v128_t) __LLVMNET_SIMD_PURE("q15mulr");
v128_t wasm_f32x4_min(v128_t, v128_t) __LLVMNET_SIMD_PURE("min_f32");
v128_t wasm_f32x4_max(v128_t, v128_t) __LLVMNET_SIMD_PURE("max_f32");
v128_t wasm_f32x4_pmin(v128_t, v128_t) __LLVMNET_SIMD_PURE("pmin_f32");
v128_t wasm_f32x4_eq(v128_t, v128_t) __LLVMNET_SIMD_PURE("eq_f32");
v128_t wasm_i32x4_trunc_sat_f32x4(v128_t) __LLVMNET_SIMD_PURE("trunc_sat_i32");
v128_t wasm_u32x4_trunc_sat_f32x4(v128_t) __LLVMNET_SIMD_PURE("trunc_sat_u32");
#undef __LLVMNET_SIMD_PURE
#ifdef __cplusplus
}
#endif

#define wasm_i8x16_const_splat(value) wasm_i8x16_splat(value)
#define wasm_i16x8_const_splat(value) wasm_i16x8_splat(value)
#define __LLVMNET_LANE(lane, count) ((void)sizeof(char[(__builtin_constant_p(lane) && (lane) >= 0 && (lane) < (count)) ? 1 : -1]))
#define wasm_v128_load64_lane(address, value, lane) (__LLVMNET_LANE(lane, 2), __llvmnet_load64_lane(address, value, lane))
#define wasm_v128_store64_lane(address, value, lane) (__LLVMNET_LANE(lane, 2), __llvmnet_store64_lane(address, value, lane))
#define __LLVMNET_BYTES8(lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7) \
    ((uint64_t)(uint8_t)(lane0) | ((uint64_t)(uint8_t)(lane1) << 8) | ((uint64_t)(uint8_t)(lane2) << 16) | \
     ((uint64_t)(uint8_t)(lane3) << 24) | ((uint64_t)(uint8_t)(lane4) << 32) | ((uint64_t)(uint8_t)(lane5) << 40) | \
     ((uint64_t)(uint8_t)(lane6) << 48) | ((uint64_t)(uint8_t)(lane7) << 56))
#define wasm_u8x16_make(lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7, lane8, lane9, lane10, lane11, lane12, lane13, lane14, lane15) \
    __llvmnet_make((int64_t)__LLVMNET_BYTES8(lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7), \
                  (int64_t)__LLVMNET_BYTES8(lane8, lane9, lane10, lane11, lane12, lane13, lane14, lane15))
#define wasm_i8x16_shuffle(left, right, lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7, lane8, lane9, lane10, lane11, lane12, lane13, lane14, lane15) \
    (__LLVMNET_LANE(lane0, 32), __LLVMNET_LANE(lane1, 32), __LLVMNET_LANE(lane2, 32), __LLVMNET_LANE(lane3, 32), \
     __LLVMNET_LANE(lane4, 32), __LLVMNET_LANE(lane5, 32), __LLVMNET_LANE(lane6, 32), __LLVMNET_LANE(lane7, 32), \
     __LLVMNET_LANE(lane8, 32), __LLVMNET_LANE(lane9, 32), __LLVMNET_LANE(lane10, 32), __LLVMNET_LANE(lane11, 32), \
     __LLVMNET_LANE(lane12, 32), __LLVMNET_LANE(lane13, 32), __LLVMNET_LANE(lane14, 32), __LLVMNET_LANE(lane15, 32), \
     __llvmnet_shuffle(left, right, (int64_t)__LLVMNET_BYTES8(lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7), \
                      (int64_t)__LLVMNET_BYTES8(lane8, lane9, lane10, lane11, lane12, lane13, lane14, lane15)))
#define wasm_i16x8_shuffle(left, right, lane0, lane1, lane2, lane3, lane4, lane5, lane6, lane7) \
    wasm_i8x16_shuffle(left, right, 2*(lane0), 2*(lane0)+1, 2*(lane1), 2*(lane1)+1, 2*(lane2), 2*(lane2)+1, 2*(lane3), 2*(lane3)+1, \
                      2*(lane4), 2*(lane4)+1, 2*(lane5), 2*(lane5)+1, 2*(lane6), 2*(lane6)+1, 2*(lane7), 2*(lane7)+1)
#define wasm_i32x4_shuffle(left, right, lane0, lane1, lane2, lane3) \
    wasm_i16x8_shuffle(left, right, 2*(lane0), 2*(lane0)+1, 2*(lane1), 2*(lane1)+1, 2*(lane2), 2*(lane2)+1, 2*(lane3), 2*(lane3)+1)
#define wasm_i64x2_shuffle(left, right, lane0, lane1) \
    wasm_i32x4_shuffle(left, right, 2*(lane0), 2*(lane0)+1, 2*(lane1), 2*(lane1)+1)
#endif