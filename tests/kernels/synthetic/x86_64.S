/* The synthetic kernel package's x86-64 kernels (System V), assembled by Clang for tests/native-kernels.sh. */
    .text
/* void sk_add_i32(int32_t *dst, const int32_t *a, const int32_t *b, size_t n): four lanes at a time with SSE2. */
    .globl sk_add_i32
    .type sk_add_i32, @function
sk_add_i32:
    xor %eax, %eax
1:  lea 4(%rax), %r8
    cmp %rcx, %r8
    ja 2f
    movdqu (%rsi,%rax,4), %xmm0
    movdqu (%rdx,%rax,4), %xmm1
    paddd %xmm1, %xmm0
    movdqu %xmm0, (%rdi,%rax,4)
    mov %r8, %rax
    jmp 1b
2:  cmp %rcx, %rax
    jae 3f
    mov (%rsi,%rax,4), %r8d
    add (%rdx,%rax,4), %r8d
    mov %r8d, (%rdi,%rax,4)
    inc %rax
    jmp 2b
3:  ret
    .size sk_add_i32, .-sk_add_i32

/* double sk_dot_f64(const double *a, const double *b, size_t n): in order, one product at a time. */
    .globl sk_dot_f64
    .type sk_dot_f64, @function
sk_dot_f64:
    xorpd %xmm0, %xmm0
    xor %eax, %eax
1:  cmp %rdx, %rax
    jae 2f
    movsd (%rdi,%rax,8), %xmm1
    mulsd (%rsi,%rax,8), %xmm1
    addsd %xmm1, %xmm0
    inc %rax
    jmp 1b
2:  ret
    .size sk_dot_f64, .-sk_dot_f64

/* int16_t sk_mix(int8_t x, uint16_t y, int64_t z, float w): x + y + z + (int64_t)w. It relies on the caller having
   extended x and y to 32 bits, as the System V convention says. */
    .globl sk_mix
    .type sk_mix, @function
sk_mix:
    movslq %edi, %rax
    mov %esi, %ecx
    add %rcx, %rax
    add %rdx, %rax
    cvttss2si %xmm0, %rcx
    add %rcx, %rax
    movswl %ax, %eax
    ret
    .size sk_mix, .-sk_mix

/* int64_t sk_weighted(const int16_t *x): the sum of x[i] * sk_weights[i] over eight i, times sk_scale. Both are the
   program's data, which the kernel reads in the package's copy. */
    .globl sk_weighted
    .type sk_weighted, @function
sk_weighted:
    lea sk_weights(%rip), %rsi
    xor %eax, %eax
    xor %ecx, %ecx
1:  movswq (%rdi,%rcx,2), %rdx
    movswq (%rsi,%rcx,2), %r8
    imul %r8, %rdx
    add %rdx, %rax
    inc %rcx
    cmp $8, %rcx
    jb 1b
    movslq sk_scale(%rip), %rdx
    imul %rdx, %rax
    ret
    .size sk_weighted, .-sk_weighted

/* void sk_count(int32_t index): increments sk_counts[index & 3], the package's data, which the program reads too. */
    .globl sk_count
    .type sk_count, @function
sk_count:
    and $3, %edi
    lea sk_counts(%rip), %rax
    incl (%rax,%rdi,4)
    ret
    .size sk_count, .-sk_count

    .data
    .globl sk_counts
    .type sk_counts, @object
    .p2align 2
sk_counts:
    .long 10, 20, 30, 40
    .size sk_counts, 16

    .section .note.GNU-stack, "", @progbits
