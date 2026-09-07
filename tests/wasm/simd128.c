#include <stdint.h>
#include <wasm_simd128.h>

static uint8_t input[129];
static uint8_t output[21][16];

uint8_t *input_address(void)
{
    return input + 1;
}

uint8_t *output_address(void)
{
    return output[0];
}

static v128_t canonicalize_nan(v128_t value)
{
    return wasm_v128_bitselect(value, wasm_i32x4_splat(0x7fc00000), wasm_f32x4_eq(value, value));
}

int run(void)
{
    const uint8_t *data = input + 1;
    v128_t left = wasm_v128_load(data);
    v128_t right = wasm_v128_load(data + 16);
    v128_t indices = wasm_v128_load(data + 32);
    v128_t signed_left = wasm_v128_load(data + 48);
    v128_t signed_right = wasm_v128_load(data + 64);
    v128_t float_left = wasm_v128_load(data + 80);
    v128_t float_right = wasm_v128_load(data + 96);
    v128_t conversions = wasm_v128_load(data + 112);
    int count = 0;
#define RECORD(value) wasm_v128_store(output[count++], (value))
    RECORD(left);
    RECORD(wasm_i8x16_add(left, right));
    RECORD(wasm_i16x8_mul(signed_left, signed_right));
    RECORD(wasm_i8x16_shl(left, 9));
    RECORD(wasm_i16x8_shr(signed_left, 19));
    RECORD(wasm_i8x16_swizzle(left, indices));
    RECORD(wasm_v128_bitselect(left, right, indices));
    RECORD(wasm_i32x4_splat(wasm_i8x16_bitmask(left)));
    RECORD(wasm_i32x4_splat(wasm_i8x16_all_true(left)));
    RECORD(wasm_i32x4_splat(wasm_v128_any_true(left)));
    RECORD(wasm_u8x16_add_sat(left, right));
    RECORD(wasm_i8x16_narrow_i16x8(signed_left, signed_right));
    RECORD(wasm_u8x16_narrow_i16x8(signed_left, signed_right));
    RECORD(canonicalize_nan(wasm_f32x4_min(float_left, float_right)));
    RECORD(canonicalize_nan(wasm_f32x4_max(float_left, float_right)));
    RECORD(wasm_i32x4_trunc_sat_f32x4(conversions));
    RECORD(wasm_u32x4_trunc_sat_f32x4(conversions));
    RECORD(wasm_i16x8_q15mulr_sat(signed_left, signed_right));
    RECORD(wasm_i32x4_extend_low_i16x8(signed_left));
    RECORD(wasm_f32x4_pmin(float_left, float_right));
    RECORD(wasm_i8x16_shuffle(left, right, 0, 16, 1, 17, 2, 18, 15, 31, 7, 23, 8, 24, 3, 19, 4, 20));
#undef RECORD
    return count;
}