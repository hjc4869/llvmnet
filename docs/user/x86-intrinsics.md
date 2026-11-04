# x86 intrinsics

Code written against Clang's x86 intrinsic headers runs in both runtime modes, from SSE to SSE4.2, with POPCNT and PCLMULQDQ: `xmmintrin.h`, `emmintrin.h`, `pmmintrin.h`, `tmmintrin.h`, `smmintrin.h`, `nmmintrin.h` and `_mm_clmulepi64_si128`, and the MMX functions of `mmintrin.h`, which Clang implements with SSE2. The portable image stays one file for every host: on x64 the intrinsics run as the processor's instructions, and on ARM64 and other hosts as compositions with the same results.

Clang lowers about half of these intrinsics to generic LLVM vector IR, which the typed vector lowering translates to `Vector128<T>` code ([ADR-16](../plan/decisions.md#adr-16-simd-levels) Level 1). The others stay x86 target intrinsics (`llvm.x86.*`), such as `psadbw`, `pmaddwd`, `pshufb`, the saturating packs, `minps`, `cvtps2dq`, `dpps`, `ptest`, `crc32` and the SSE4.2 string comparisons. Each of those becomes a call of a helper in [src/LlvmNet.Runtime/Simd/X86.cs](../../src/LlvmNet.Runtime/Simd/X86.cs) (Level 2), which the JIT and NativeAOT inline. A helper runs the x86 instruction behind an explicit `IsSupported` test, the AdvSimd instruction of the same meaning on ARM64 where one exists, and a composition of `Vector128` operations elsewhere.

## Selecting the level

```sh
bin/llvmnet --isa=x86-64-v2 -O2 kernel.c -o kernel.dll                    # portable
bin/llvmnet -msse4.1 -mpopcnt -O2 kernel.c -o kernel.dll                   # portable, a build system's flags
bin/llvmnet --runtime=system -O2 kernel.c -o kernel.dll                    # system mode: SSE2, as native x86-64
bin/llvmnet --runtime=system --isa=x86-64-v2 -O2 kernel.c -o kernel.dll    # system mode: up to SSE4.2
```

Portable mode compiles for its fixed data layout without the x86 macros, so libraries take their C paths. `--isa=x86-64-v2` gives the frontend what `-march=x86-64-v2` gives native code: the macros `__x86_64__`, `__SSE__` to `__SSE4_2__`, `__POPCNT__` and `__MMX__`, the intrinsics, and the vectorizers' costs at that level. It does not change the data ABI: layouts, `long double` and the calling convention stay `dotnet64-v2`. It leaves out CMPXCHG16B (`-mno-cx16`), so 16-byte atomics stay the library calls that objects of every level share. In portable mode the x86 SIMD feature flags up to that level are its aliases, so a library's build needs no change: `-msse`, `-msse2`, `-msse3`, `-mssse3`, `-msse4`, `-msse4.1`, `-msse4.2`, `-mpopcnt`, `-mpclmul`, `-mcrc32` and `-march=x86-64-v2` select `x86-64-v2` and reach Clang as given, so `-mpclmul` also enables PCLMULQDQ. `-mno-sse3` and the other negations above SSE2 pass through without changing the level; `-mno-sse` and `-mno-sse2` would move floating-point arguments out of the SSE registers and are rejected, as are the flags of `x86-64-v3` (`-mavx`, `-mavx2`, `-mfma` and the others), whose intrinsics come with SV-09. In system mode the `-m` and `-march` options work as natively, and `--isa=x86-64-v2` stands for `-march=x86-64-v2 -mno-cx16`.

The level is recorded in each object compiled with `--isa` or one of its portable aliases (the `llvmnet.isa` module flag) and, for a program with objects compiled above the baseline, in the image's `llvmnet.isa` assembly metadata. Objects of different levels link together; the level decides only which intrinsics and macros a unit sees.

## NativeAOT

A NativeAOT executable whose objects were compiled at `x86-64-v2` gets the NativeAOT compiler's `x86-64-v2` instruction set: it requires an x64 processor with SSE4.2 and POPCNT (since about 2009), as native code compiled with `-march=x86-64-v2` does. `--aot-instruction-set baseline` keeps SSE2, with SSE4.2 as an opportunistic set: the helpers then test it at run time, but the generic `Vector128` operations of the same code are compiled for SSE2, and astc-encoder's SSE4.1 build compresses 1.8 times slower there than at `x86-64-v2` ([evidence](../evidence/2026-11-04-x86-intrinsics.md#nativeaot-instruction-sets)). `x86-64-v3` and `native` select more. Relocatable projects (`--aot-project`) apply the x64 levels only when they are published for an x64 runtime identifier.

## Exactness

The helpers give the instructions' results bit for bit: `minps` and `maxps` return the second operand when either is NaN, the conversions return the integer indefinite (`0x80000000`) for NaN and out-of-range inputs and round to nearest even, `psadbw` sums into 64-bit lanes, `pmulhrsw` rounds as the instruction does, and the string comparisons follow the instruction's pseudo-code. Two differences are allowed:

- NaN payloads. Where a composition adds or multiplies two NaNs (`haddps`, `addsubps`, `dpps` and `dppd` without SSE3 or SSE4.1, and on ARM64), the payload of the result follows the host, as LLVM's semantics allow; ARM64 also produces a positive default NaN where x86 produces a negative one. Clang's own `-O2` code for `dppd` differs from the instruction in the same way.
- `rcpps` and `rsqrtps` are approximations whose bits differ between processors; composed, they divide, within the instructions' documented error of 1.5 × 2⁻¹².

Translated code runs with the default MXCSR: round to nearest, all exceptions masked, no flush to zero. `_mm_getcsr` returns `0x1F80`, and `_mm_setcsr` and the `_MM_SET_*` macros are not translated.

## What is not translated

AVX and later, AES, SHA, `monitor`/`mwait` and the MXCSR write. Compiling a unit that contains one prints a warning naming the intrinsic and its function, and linking a program that reaches it fails with the same message; a unit whose x86 code no program reaches still links. `-w` silences the warning. The message suggests the code's SSE4.2, SSE2 or C path.

## CPU detection

Libraries that choose their SIMD paths at run time ask the processor with the CPUID instruction, through inline assembly or `<cpuid.h>`. System mode answers those queries through native thunks, including `<cpuid.h>`'s `__cpuid` and `__cpuid_count`. Portable mode rejects the inline assembly, and `__builtin_cpu_supports` does not link in either mode; both come with SV-12 ([plan](../plan/12-simd.md)). Until then, build such a library with its compile-time selection in portable mode, for example `-DWUFFS_CONFIG__AVOID_CPU_ARCH` for wuffs; a library without one, such as Chromium's zlib, needs its feature check replaced. A library that tests only the macros, such as abseil's swiss tables or astc-encoder's core, needs nothing.

## Speed

astc-encoder's SSE4.1 build compresses a 1024 × 1024 photograph 2.8 times faster than its scalar build under the JIT and 3.4 times as NativeAOT, at 1.4 times native SSE4.1. Chromium's zlib deflates 1.35 to 1.6 times faster with its SIMD paths and checksums 1.5 to 6 times faster ([evidence](../evidence/2026-11-04-x86-intrinsics.md)).

## Tests

```sh
tests/run.sh x86-intrinsics
```

[tests/x86-intrinsics.sh](../../tests/x86-intrinsics.sh) runs [tests/c/x86-sse-conformance.c](../../tests/c/x86-sse-conformance.c), which calls every helper by its LLVM name on random and special inputs at every immediate, and a program [tests/c/x86-headers.py](../../tests/c/x86-headers.py) writes from Clang's headers that calls every intrinsic in them. Both must print what the native instructions print: bit for bit under the JIT, as NativeAOT and in system mode, and with NaN results canonicalized where the compositions run (`DOTNET_EnableSSE42=0`, which leaves SSE2, and `DOTNET_EnableHWIntrinsic=0`). The cross-platform bundle runs the conformance program on Windows x64, macOS ARM64 and Linux ARM64 ([tests/xp/fixtures.tsv](../../tests/xp/fixtures.tsv)).
