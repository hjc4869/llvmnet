/* ffmpeg's libavutil/x86/cpuid.asm in C, for the browser profile with its x86 kernel package (FFMPEG_X86ASM=1 in
   scripts/build-ffmpeg.sh). llvmnet compiles it with the program, so it runs on every host: CPUID and XGETBV report the
   running host's features (docs/user/x86-intrinsics.md#cpu-detection), none on an ARM64 host, where ffmpeg selects its
   C functions and never calls a kernel. The package leaves out the assembly versions. */
void ff_cpu_cpuid(int index, int *eax, int *ebx, int *ecx, int *edx)
{
    int a, b, c, d;
    __asm__ volatile("cpuid" : "=a"(a), "=b"(b), "=c"(c), "=d"(d) : "a"(index), "c"(0));
    *eax = a;
    *ebx = b;
    *ecx = c;
    *edx = d;
}

void ff_cpu_xgetbv(int op, int *eax, int *edx)
{
    int a, d;
    __asm__(".byte 0x0f, 0x01, 0xd0" : "=a"(a), "=d"(d) : "c"(op));
    *eax = a;
    *edx = d;
}
