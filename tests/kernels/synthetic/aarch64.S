/* The synthetic kernel package's AArch64 kernels (AAPCS64), assembled by Clang for tests/native-kernels.sh. */
    .text
/* void sk_add_i32(int32_t *dst, const int32_t *a, const int32_t *b, size_t n): four lanes at a time with NEON. */
    .globl sk_add_i32
    .type sk_add_i32, %function
sk_add_i32:
    mov x4, #0
1:  add x5, x4, #4
    cmp x5, x3
    b.hi 2f
    add x6, x1, x4, lsl #2
    add x7, x2, x4, lsl #2
    ldr q0, [x6]
    ldr q1, [x7]
    add v0.4s, v0.4s, v1.4s
    add x6, x0, x4, lsl #2
    str q0, [x6]
    mov x4, x5
    b 1b
2:  cmp x4, x3
    b.hs 3f
    ldr w5, [x1, x4, lsl #2]
    ldr w6, [x2, x4, lsl #2]
    add w5, w5, w6
    str w5, [x0, x4, lsl #2]
    add x4, x4, #1
    b 2b
3:  ret
    .size sk_add_i32, .-sk_add_i32

/* double sk_dot_f64(const double *a, const double *b, size_t n): in order, one product at a time. */
    .globl sk_dot_f64
    .type sk_dot_f64, %function
sk_dot_f64:
    movi d0, #0
    mov x3, #0
1:  cmp x3, x2
    b.hs 2f
    ldr d1, [x0, x3, lsl #3]
    ldr d2, [x1, x3, lsl #3]
    fmul d1, d1, d2
    fadd d0, d0, d1
    add x3, x3, #1
    b 1b
2:  ret
    .size sk_dot_f64, .-sk_dot_f64

/* int16_t sk_mix(int8_t x, uint16_t y, int64_t z, float w): x + y + z + (int64_t)w; AAPCS64 leaves the extension of
   narrow arguments to the callee. */
    .globl sk_mix
    .type sk_mix, %function
sk_mix:
    sxtb x0, w0
    and x1, x1, #0xffff
    add x0, x0, x1
    add x0, x0, x2
    fcvtzs x3, s0
    add x0, x0, x3
    sxth w0, w0
    ret
    .size sk_mix, .-sk_mix

/* int64_t sk_weighted(const int16_t *x): the sum of x[i] * sk_weights[i] over eight i, times sk_scale. Both are the
   program's data, which the kernel reads in the package's copy. */
    .globl sk_weighted
    .type sk_weighted, %function
sk_weighted:
    adrp x1, sk_weights
    add x1, x1, :lo12:sk_weights
    mov x2, #0
    mov x3, #0
1:  ldrsh x4, [x0, x3, lsl #1]
    ldrsh x5, [x1, x3, lsl #1]
    madd x2, x4, x5, x2
    add x3, x3, #1
    cmp x3, #8
    b.lo 1b
    adrp x1, sk_scale
    ldrsw x4, [x1, :lo12:sk_scale]
    mul x0, x2, x4
    ret
    .size sk_weighted, .-sk_weighted

/* void sk_count(int32_t index): increments sk_counts[index & 3], the package's data, which the program reads too. */
    .globl sk_count
    .type sk_count, %function
sk_count:
    and w0, w0, #3
    adrp x1, sk_counts
    add x1, x1, :lo12:sk_counts
    ldr w2, [x1, w0, uxtw #2]
    add w2, w2, #1
    str w2, [x1, w0, uxtw #2]
    ret
    .size sk_count, .-sk_count

    .data
    .globl sk_counts
    .type sk_counts, %object
    .p2align 2
sk_counts:
    .word 10, 20, 30, 40
    .size sk_counts, 16

    .section .note.GNU-stack, "", %progbits
