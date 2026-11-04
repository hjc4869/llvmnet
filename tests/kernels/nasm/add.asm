; A kernel in nasm syntax for tests/system-objects.sh: int64_t nk_add3(int64_t a, int64_t b, int64_t c) returns
; (a + b + c + nk_bias) * NK_SCALE, where NK_SCALE comes from the command line (-D) and nk_bias is a variable this file
; defines and the program changes.
default rel
section .text
global nk_add3:function
nk_add3:
    lea rax, [rdi + rsi]
    add rax, rdx
    add rax, [nk_bias]
    imul rax, rax, NK_SCALE
    ret

section .data
global nk_bias:data
nk_bias: dq 1000

section .note.GNU-stack noalloc noexec nowrite progbits
