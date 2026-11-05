# x86 intrinsics

Code written against Clang's x86 intrinsic headers runs in both runtime modes, from SSE to AVX2, with FMA, F16C, BMI1, BMI2, LZCNT, POPCNT and PCLMULQDQ: `xmmintrin.h`, `emmintrin.h`, `pmmintrin.h`, `tmmintrin.h`, `smmintrin.h`, `nmmintrin.h`, `avxintrin.h`, `avx2intrin.h`, `fmaintrin.h`, `f16cintrin.h`, `bmiintrin.h`, `bmi2intrin.h`, `lzcntintrin.h` and `_mm_clmulepi64_si128`, and the MMX functions of `mmintrin.h`, which Clang implements with SSE2. The portable image stays one file for every host: on x64 the intrinsics run as the processor's instructions, and on ARM64 and other hosts as compositions with the same results.

Clang lowers about half of these intrinsics to generic LLVM vector IR, which the typed vector lowering translates to `Vector128<T>` code ([ADR-16](../plan/decisions.md#adr-16-simd-levels) Level 1). The others stay x86 target intrinsics (`llvm.x86.*`), such as `psadbw`, `pmaddwd`, `pshufb`, the saturating packs, `minps`, `cvtps2dq`, `dpps`, `ptest`, `crc32` and the SSE4.2 string comparisons. Each of those becomes a call of a helper in [src/LlvmNet.Runtime/Simd/X86.cs](../../src/LlvmNet.Runtime/Simd/X86.cs) or, for x86-64-v3's, [X86Avx.cs](../../src/LlvmNet.Runtime/Simd/X86Avx.cs) (Level 2), which the JIT and NativeAOT inline. A helper runs the x86 instruction behind an explicit `IsSupported` test, the AdvSimd instruction of the same meaning on ARM64 where one exists, and a composition of `Vector128` operations elsewhere. A 256-bit helper without AVX or AVX2 runs the 128-bit helper on each half, which is exact because those instructions work within 128-bit lanes; the permutes, gathers, masked loads and stores and tests across lanes have compositions of their own. .NET does not accelerate `Vector256` without AVX2, so the fallbacks never compute with whole `Vector256` values.

[simd.md](simd.md) puts the intrinsics beside the other forms of vector code (the vectorizers, vector extensions, assembly) and shows what to expect per host; `--report-simd` lists the helpers a program calls with the instruction sets each one uses on x64 and ARM64.

## Selecting the level

```sh
bin/llvmnet --isa=x86-64-v2 -O2 kernel.c -o kernel.dll                    # portable, SSE4.2
bin/llvmnet --isa=x86-64-v3 -O2 kernel.c -o kernel.dll                    # portable, AVX2 and FMA
bin/llvmnet -msse4.1 -mpopcnt -O2 kernel.c -o kernel.dll                   # portable, a build system's flags
bin/llvmnet --runtime=system -O2 kernel.c -o kernel.dll                    # system mode: SSE2, as native x86-64
bin/llvmnet --runtime=system --isa=x86-64-v2 -O2 kernel.c -o kernel.dll    # system mode: up to SSE4.2
```

Portable mode compiles for its fixed data layout without the x86 macros, so libraries take their C paths. `--isa=x86-64-v2` gives the frontend what `-march=x86-64-v2` gives native code: the macros `__x86_64__`, `__SSE__` to `__SSE4_2__`, `__POPCNT__` and `__MMX__`, the intrinsics, and the vectorizers' costs at that level. It does not change the data ABI: layouts, `long double` and the calling convention stay `dotnet64-v2`. It leaves out CMPXCHG16B (`-mno-cx16`), so 16-byte atomics stay the library calls that objects of every level share. In portable mode the x86 SIMD feature flags up to that level are its aliases, so a library's build needs no change: `-msse`, `-msse2`, `-msse3`, `-mssse3`, `-msse4`, `-msse4.1`, `-msse4.2`, `-mpopcnt`, `-mpclmul`, `-mcrc32` and `-march=x86-64-v2` select `x86-64-v2` and reach Clang as given, so `-mpclmul` also enables PCLMULQDQ. `--isa=x86-64-v3` adds AVX, AVX2, FMA, F16C, BMI1, BMI2, LZCNT and MOVBE as `-march=x86-64-v3` does, and its flags are aliases of it: `-mavx`, `-mavx2`, `-mfma`, `-mf16c`, `-mbmi`, `-mbmi2`, `-mlzcnt`, `-mmovbe` and `-march=x86-64-v3`; the highest level a command line names applies. `-mno-sse3` and the other negations above SSE2, and `-march=x86-64` (the baseline itself, which skcms puts before `-mavx2 -mf16c`), pass through without changing the level; `-mno-sse` and `-mno-sse2` would move floating-point arguments out of the SSE registers and are rejected, as are AVX-512's flags (`-mavx512f`, `-march=x86-64-v4`), whose intrinsics the runtime does not implement. In system mode the `-m` and `-march` options work as natively, and `--isa=x86-64-v2` and `x86-64-v3` stand for `-march=<level> -mno-cx16`. `x86-64-v3` also holds 256-bit vectors whole (`--vector-width=256`), which runs faster on the AVX2 hosts the level targets and slower elsewhere; `--vector-width=128` keeps two halves.

The level is recorded in each object compiled with `--isa` or one of its portable aliases (the `llvmnet.isa` module flag) and, for a program with objects compiled above the baseline, in the image's `llvmnet.isa` assembly metadata. Objects of different levels link together; the level decides only which intrinsics and macros a unit sees.

## NativeAOT

A NativeAOT executable whose objects were compiled at `x86-64-v2` gets the NativeAOT compiler's `x86-64-v2` instruction set: it requires an x64 processor with SSE4.2 and POPCNT (since about 2009), as native code compiled with `-march=x86-64-v2` does; one with objects at `x86-64-v3` gets `x86-64-v3` and requires AVX2, BMI2 and FMA (since about 2015). `--aot-instruction-set baseline` keeps SSE2, with SSE4.2 as an opportunistic set: the helpers then test it at run time, but the generic `Vector128` operations of the same code are compiled for SSE2, and astc-encoder's SSE4.1 build compresses 1.8 times slower there than at `x86-64-v2` ([evidence](../evidence/2026-11-04-x86-intrinsics.md#nativeaot-instruction-sets)). `x86-64-v3` and `native` select more. Relocatable projects (`--aot-project`) apply the x64 levels only when they are published for an x64 runtime identifier.

## Exactness

The helpers give the instructions' results bit for bit: `minps` and `maxps` return the second operand when either is NaN, the conversions return the integer indefinite (`0x80000000`) for NaN and out-of-range inputs and round to nearest even, `psadbw` sums into 64-bit lanes, `pmulhrsw` rounds as the instruction does, and the string comparisons follow the instruction's pseudo-code. Two differences are allowed:

- NaN payloads. Where a composition adds or multiplies two NaNs (`haddps`, `addsubps`, `dpps` and `dppd` without SSE3 or SSE4.1, and on ARM64), the payload of the result follows the host, as LLVM's semantics allow; ARM64 also produces a positive default NaN where x86 produces a negative one. Clang's own `-O2` code for `dppd` differs from the instruction in the same way.
- `rcpps` and `rsqrtps` are approximations whose bits differ between processors; composed, they divide, within the instructions' documented error of 1.5 × 2⁻¹².
- Fused multiply-adds with several NaN operands. RyuJIT picks the form of an FMA instruction by register allocation, and the form decides which NaN propagates.

`llvm.fmuladd`, which Clang emits for `a * b + c` under its default `-ffp-contract=on`, is rounded once in functions whose target has FMA (`--isa=x86-64-v3`, `-mfma`, `target("fma")`), as native code does there, and twice elsewhere, as native code without FMA does. `vcvtps2ph` (F16C), which .NET has no intrinsic for, is computed exactly in every rounding mode.

Translated code runs with the default MXCSR: round to nearest, all exceptions masked, no flush to zero. `_mm_getcsr` returns `0x1F80`, and `_mm_setcsr` and the `_MM_SET_*` macros are not translated.

## What is not translated

AVX-512 and later, AES, SHA, `monitor`/`mwait` and the MXCSR write; and code that computes with `_Float16` (a `half` type in LLVM IR), such as skcms's Haswell transform. Compiling a unit that contains one of the intrinsics prints a warning naming it and its function, and linking a program that reaches it fails with the same message; a unit whose x86 code no program reaches still links. `-w` silences the warning. The message suggests the code's AVX2, SSE or C path.

## CPU detection

Libraries that choose their SIMD paths at run time ask the processor, and their detection code works unchanged in both modes:

- **CPUID and XGETBV inline assembly**, in the spellings of `<cpuid.h>` (`__get_cpuid`, `__cpuid_count`), ffmpeg, LLVM and Eigen and the plain `cpuid` that names EBX. System mode runs them in native thunks. In portable mode the runtime answers: on x64 with the host's own CPUID, and on ARM64, or with .NET's hardware intrinsics off, with nothing (a maximum leaf of 0), so a library selects its x86 paths only where they run as x86 instructions. XCR0 is the processor's supported state without the AVX and AVX-512 components where .NET does not use them: under `DOTNET_EnableAVX=0` and in a NativeAOT executable whose instruction set has no AVX (the baseline and `x86-64-v2`), a library sees an operating system without AVX state and takes its SSE paths.
- **`__builtin_cpu_supports`, `__builtin_cpu_is` and `__builtin_cpu_init`**, from compiler-rt's CPU model, which both sysroots carry and which reads the same CPUID.
- **`target_clones`** functions, which call the clone the CPU model selects. In system mode they are GNU indirect functions; llvmnet resolves every ifunc in a constructor that runs before the program's own, as the dynamic loader would. The portable target is not Linux, where Clang ignores the `ifunc` attribute itself (with a warning), so a hand-written ifunc stays unresolved there.
- **`getauxval(AT_HWCAP)`** returns what Linux gives native processes: CPUID leaf 1's EDX on x64 and the `HWCAP_*` bits of the instruction sets .NET reports on ARM64.
- **`<llvmnet/cpu.h>`**: `llvmnet_cpu_features()` returns the instruction sets the host executes as .NET reports them (`LLVMNET_CPU_SSE4_2`, `LLVMNET_CPU_AVX2`, `LLVMNET_CPU_NEON` and the others), for a program that wants to ask without CPUID.

ffmpeg's `ff_get_cpu_flags_x86`, Chromium zlib's `cpu_features.c`, wuffs and astc-encoder's command-line check report the host's features in both modes ([evidence](../evidence/2026-11-04-cpu-detection.md)). Detection written in assembly files, such as dav1d's and ffmpeg's `cpuid.asm`, runs from a [native kernel package](native-kernels.md).

## Speed

astc-encoder's SSE4.1 build compresses a 1024 × 1024 photograph 2.8 times faster than its scalar build under the JIT and 3.4 times as NativeAOT, at 1.4 times native SSE4.1. libjxl with Highway's AVX2 target encodes a 4K image at effort 7 4.4 times faster than its scalar build as NativeAOT, at 2.1 times native AVX2 ([evidence](../evidence/2026-11-04-avx2-intrinsics.md#libjxl)). Chromium's zlib deflates 1.35 to 1.6 times faster with its SIMD paths and checksums 1.5 to 6 times faster ([evidence](../evidence/2026-11-04-x86-intrinsics.md)).

## Tests

```sh
tests/run.sh x86-intrinsics
```

[tests/x86-intrinsics.sh](../../tests/x86-intrinsics.sh) runs [tests/c/x86-sse-conformance.c](../../tests/c/x86-sse-conformance.c) and [tests/c/x86-avx-conformance.c](../../tests/c/x86-avx-conformance.c), which call every helper by its LLVM name on random and special inputs at every immediate, and the programs [tests/c/x86-headers.py](../../tests/c/x86-headers.py) writes from Clang's headers of each level that call every intrinsic in them. Both must print what the native instructions print: bit for bit under the JIT, as NativeAOT and in system mode, and with NaN results canonicalized where the compositions run (`DOTNET_EnableSSE42=0`, which leaves SSE2, `DOTNET_EnableAVX=0`, `DOTNET_EnableAVX2=0`, `DOTNET_EnableFMA=0`, `DOTNET_EnableBMI2=0` and `DOTNET_EnableHWIntrinsic=0`). The cross-platform bundle runs the conformance program on Windows x64, macOS ARM64 and Linux ARM64 ([tests/xp/fixtures.tsv](../../tests/xp/fixtures.tsv)).
