/* The synthetic kernel package's x86-64 kernels for Windows, assembled by Clang for tests/native-kernels.sh. The
   Microsoft x64 convention passes arguments in RCX, RDX, R8 and R9 or XMM0 to XMM3 by position, leaves the extension of
   narrow arguments to the callee and preserves RSI, RDI and XMM6 to XMM15, which these leaf functions do not touch. */
    .text
/* void sk_add_i32(int32_t *dst, const int32_t *a, const int32_t *b, size_t n): four lanes at a time with SSE2. */
    .globl sk_add_i32
    .def sk_add_i32; .scl 2; .type 32; .endef
sk_add_i32:
    xor %eax, %eax
1:  lea 4(%rax), %r10
    cmp %r9, %r10
    ja 2f
    movdqu (%rdx,%rax,4), %xmm0
    movdqu (%r8,%rax,4), %xmm1
    paddd %xmm1, %xmm0
    movdqu %xmm0, (%rcx,%rax,4)
    mov %r10, %rax
    jmp 1b
2:  cmp %r9, %rax
    jae 3f
    mov (%rdx,%rax,4), %r10d
    add (%r8,%rax,4), %r10d
    mov %r10d, (%rcx,%rax,4)
    inc %rax
    jmp 2b
3:  ret

/* double sk_dot_f64(const double *a, const double *b, size_t n): in order, one product at a time. */
    .globl sk_dot_f64
    .def sk_dot_f64; .scl 2; .type 32; .endef
sk_dot_f64:
    xorpd %xmm0, %xmm0
    xor %eax, %eax
1:  cmp %r8, %rax
    jae 2f
    movsd (%rcx,%rax,8), %xmm1
    mulsd (%rdx,%rax,8), %xmm1
    addsd %xmm1, %xmm0
    inc %rax
    jmp 1b
2:  ret

/* int16_t sk_mix(int8_t x, uint16_t y, int64_t z, float w): x + y + z + (int64_t)w, with w in XMM3. */
    .globl sk_mix
    .def sk_mix; .scl 2; .type 32; .endef
sk_mix:
    movsbq %cl, %rax
    movzwl %dx, %ecx
    add %rcx, %rax
    add %r8, %rax
    cvttss2si %xmm3, %rcx
    add %rcx, %rax
    movswl %ax, %eax
    ret

/* int64_t sk_weighted(const int16_t *x): the sum of x[i] * sk_weights[i] over eight i, times sk_scale. Both are the
   program's data, which the kernel reads in the package's copy. */
    .globl sk_weighted
    .def sk_weighted; .scl 2; .type 32; .endef
sk_weighted:
    lea sk_weights(%rip), %r9
    xor %eax, %eax
    xor %r10d, %r10d
1:  movswq (%rcx,%r10,2), %rdx
    movswq (%r9,%r10,2), %r8
    imul %r8, %rdx
    add %rdx, %rax
    inc %r10
    cmp $8, %r10
    jb 1b
    movslq sk_scale(%rip), %rdx
    imul %rdx, %rax
    ret

/* void sk_count(int32_t index): increments sk_counts[index & 3], the package's data, which the program reads too. */
    .globl sk_count
    .def sk_count; .scl 2; .type 32; .endef
sk_count:
    and $3, %ecx
    lea sk_counts(%rip), %rax
    incl (%rax,%rcx,4)
    ret

    .data
    .globl sk_counts
    .p2align 2
sk_counts:
    .long 10, 20, 30, 40
