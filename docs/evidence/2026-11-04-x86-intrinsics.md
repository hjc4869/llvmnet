# Evidence: SV-08, x86 intrinsics from SSE to SSE4.2 (2026-11-04)

Host: AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB, Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `17ddd0e`. The other hosts: Windows x64 on an AMD Ryzen 9 9955HX (`fluorite`), macOS on an Apple M6 (`mainmac`), and Linux ARM64 in the `mcr.microsoft.com/dotnet/runtime:10.0` arm64 container under qemu on the build host. Design: [12-simd.md](../plan/12-simd.md#level-2-target-intrinsics); user documentation: [x86-intrinsics.md](../user/x86-intrinsics.md).

## Scope

LLVM 22 defines 167 intrinsics for SSE to SSE4.2. 22 of them take or return the `x86_mmx` type: Clang 22 never emits them, because it implements every MMX function of `mmintrin.h` with SSE2 vectors since Clang 20 (the header sweep below calls all of them). `monitor` and `mwait` are not for user code. `ldmxcsr` (`_mm_setcsr`) would change the rounding mode or flush denormals, which translated code cannot do, so it stays rejected with its own message. The other 142 translate: the spin hint and the three fences, as before, and 138 new helpers. Two more were added because real code needs them: `llvm.x86.mmx.emms` (`_mm_empty`, a no-op once MMX is SSE2 code) and `llvm.x86.pclmulqdq`, which CRC code pairs with SSE4.2 (wuffs and Chromium's zlib, below) and which .NET has as `Pclmulqdq.CarrylessMultiply` and ARM64's `pmull`.

## The helpers

Each intrinsic is a method of `LlvmNet.Runtime.X86` marked `[LlvmIntrinsic("llvm.x86....")]`, typed with .NET's natural lanes; the backend calls it and reinterprets each vector operand of the same width (`Vector128.As`), with `--vector-width=256` narrowing the `Vector256` that holds a 128-bit value (`GetLower`) and widening the result (`ToVector256`). A method branches on the x86 class's `IsSupported`, then on AdvSimd's where an ARM64 instruction has the same meaning (`faddp` for `haddps`, `uzp1`/`uzp2` for the horizontal operations, `smull` and `addp` for `pmaddwd`, `uaddlp` for `psadbw`, `sqxtun` for the unsigned packs, `urhadd` for `pavgb`, `tbl` for `pshufb`, `uminv` for `phminposuw`, `pmull` for `pclmulqdq`), and composes the result from `Vector128` operations otherwise. The compositions are exact: `minps` is a select on the ordered comparison (the second operand for NaN and equal zeros), the conversions check the range and return `0x80000000`, `pmulhrsw` widens and rounds as the instruction does (`-32768 * -32768` is `-32768`), `roundps` quiets a signaling NaN, and the SSE4.2 string comparisons, which .NET has no instruction for, follow the SDM's pseudo-code (aggregation, polarity, then index, mask or flag).

An immediate operand is the call's constant, so once the JIT inlines the helper the instruction gets its immediate form: `_mm_dp_ps(a, b, 0xF1)` in a helper inlined at a constant compiles to `vdpps xmm0, xmm0, [rsp+0x18], -15` at full optimization. NativeAOT's opportunistic instruction sets are run-time tests: ILC hoists the SSE4.1 test of a loop into a register and branches around each `dpps` (`test esi, esi; jne; dpps xmm1, xmm2, -15`), with the composition on the other side.

## Conformance

[tests/c/x86-sse-conformance.c](../../tests/c/x86-sse-conformance.c) calls every helper by its LLVM name through asm labels (`v4f dpps(v4f, v4f, char) __asm__("llvm.x86.sse41.dpps")`), so the native build runs the instructions and llvmnet's the helpers, on 3,000 random inputs each with the special values mixed in (NaNs with payloads, signaling NaNs, denormals, infinities, the integer bounds, the conversion boundaries, halves for rounding) and at every immediate (all 256 for `dpps` and `insertps`, 128 for the 14 string comparisons). It prints a hash of every result bit per intrinsic and a hash with NaN results canonicalized; `rcpps` and `rsqrtps` print the number of results outside their documented error of 1.5 × 2⁻¹² (zero everywhere). `tests/x86-intrinsics.sh` checks that the program reaches exactly the helpers the runtime has.

| configuration | build | result |
| --- | --- | --- |
| JIT, this host (AVX-512 hardware, VEX encodings) | `-O0` | every bit as native `-O0` |
| JIT, `DOTNET_EnableAVX=0` | `-O0` | every bit |
| JIT, `--vector-width=256` | `-O0` | every bit |
| JIT, system mode | `-O0` | every bit |
| NativeAOT, `x86-64-v2` (the default for these objects) | `-O0` | every bit |
| NativeAOT, `--aot-instruction-set baseline` (SSE4.2 opportunistic) | `-O0` | every bit |
| NativeAOT, `x86-64-v3` | `-O0` | every bit |
| JIT, Windows x64 (Ryzen 9 9955HX) | `-O0` | every bit as this host's native build |
| JIT, `DOTNET_EnableSSE42=0` (SSE2 only) | `-O0` | NaN-canonical; `haddps`, `haddpd` and `dppd` differ in NaN payloads |
| JIT, `DOTNET_EnableHWIntrinsic=0` (software), here and on Windows | `-O0` | NaN-canonical; `dpps` and `dppd` differ in NaN payloads |
| JIT and software, Linux ARM64 (qemu) | `-O0`, `-O2` | NaN-canonical; the eight arithmetic compositions differ in NaN payloads |
| JIT, software and NativeAOT, macOS ARM64 (Apple M6) | `-O0` | NaN-canonical |
| JIT, `-O2` | `-O2` | NaN-canonical as native `-O2`, every bit as native `-O0` |

The NaN payloads differ where a composition adds or multiplies two NaNs, and are LLVM's to choose: RyuJIT treats a floating-point addition and multiplication as commutative and may swap its operands, which picks the other NaN on x86, and ARM64 propagates by other rules and produces a positive default NaN where x86 produces a negative one. Clang's own `-O2` code for `dppd` with a constant mask differs from the instruction in the same way, which is why the `-O2` comparison is NaN-canonical.

## Clang's headers

[tests/c/x86-headers.py](../../tests/c/x86-headers.py) reads Clang 22's `xmmintrin.h` to `nmmintrin.h` (with `popcntintrin.h`, `crc32intrin.h` and the `mmintrin.h` they include) from its AST and `-dM`, and writes a program that calls each of the 535 intrinsics on random inputs, the macros with immediates at several values each, pointers into aligned random buffers that it hashes after the call. About half are generic vector IR, which the typed lowering translates. Every hash matches native `-O0` under the JIT, and the NaN-canonical ones match at `-O2`, with hardware intrinsics off and as NativeAOT at `-O1`. Left out: `_mm_setcsr` and `_mm_getcsr`, the `_mm_undefined_*` functions, `monitor`/`mwait`, allocation, and `rcpps`/`rsqrtps`, whose bits are processor-specific.

## `--isa=x86-64-v2`

Portable mode keeps the x86 macros undefined, so libraries take their C paths. `--isa=x86-64-v2` passes `-march=x86-64-v2 -mno-cx16` and keeps them, with the SSE3 to SSE4.2, POPCNT and MMX ones the level adds. As the owner decided on 2026-11-03, the x86 SIMD feature flags up to that level are its aliases in portable mode (`-msse` to `-msse4.2`, `-mssse3`, `-mpopcnt`, `-mpclmul`, `-mcrc32`, `-march=x86-64-v2`), and they reach Clang as given, so `-mpclmul` adds PCLMULQDQ; the negations above SSE2 pass through, and `-mno-sse`, `-mno-sse2` and the flags of `x86-64-v3` stay rejected, the latter naming SV-09. With them the reproducers below build with their own CMake flags in portable mode. Without CMPXCHG16B, 16-byte atomics stay calls into the runtime that objects of every level share; with it a v2 object would inline `cmpxchg16b` where a baseline object takes the library's lock. The level is the object's `llvmnet.isa` module flag, with `AppendUnique` so that objects of different levels link; an image whose objects recorded one gets `llvmnet.isa` assembly metadata. Objects without a level record nothing, so baseline objects and images are unchanged: 32 images (eight programs of `tests/c` and `tests/cpp`, both modes, `-O0` and `-O2`, vector code included) are byte-identical to the images of the compiler at `17ddd0e`.

## The survey's reproducers

The library survey's system-mode builds of ANGLE needed `-DASTCENC_SSE=0`, dropped Chromium zlib's SIMD defines and failed on abseil's and astc-encoder's SSE paths ("Unsupported intrinsic: llvm.x86.sse2.psad.bw", R6, SA-12). Built from ANGLE's `third_party` with their SSE paths on, outside the GN build (scripts in `artifacts/simd-x86/`), all three now link and run in both modes with output identical to native. Median seconds of five runs, alternating:

**astc-encoder** (`astcenc -cl`, a 1024 × 1024 crop of the survey's 8K photograph, 6×6 blocks, `-medium`, one thread; ANGLE's copy, `-O3 -ffp-model=precise -ffp-contract=off`): the SSE4.1 build (`ASTCENC_SSE=41`, `ASTCENC_POPCNT=1`, `-msse4.1 -mpopcnt`) against the scalar build (`ASTCENC_SSE=0`). Every `.astc` file is byte-identical to native's. Portable mode compiles the command line's CPU check (`astcenccli_entry.cpp`) as for SSE2 and wuffs with `WUFFS_CONFIG__AVOID_CPU_ARCH`, both CPUID users, and llvmnet's builds in both modes leave out `__amd64`, under which the soft-float code uses `bsrl` inline assembly, as the survey did.

| engine | SSE4.1 | scalar | scalar / SSE4.1 | SSE4.1 / native SSE4.1 |
| --- | ---: | ---: | ---: | ---: |
| native | 1.598 | 3.571 | 2.23 | 1.00 |
| JIT, portable | 2.374 | 6.745 | 2.84 | 1.49 |
| JIT, system mode `-msse4.1 -mpopcnt` | 2.389 | 6.737 | 2.82 | 1.49 |
| NativeAOT, `x86-64-v2` (the default for these objects) | 2.424 | 8.167 (baseline) | 3.37 | 1.52 |
| NativeAOT, `--aot-instruction-set baseline` | 4.412 | 8.167 | 1.85 | 2.76 |
| NativeAOT, `x86-64-v3` | 2.390 | 7.519 | 3.15 | 1.50 |

**Chromium zlib** (ANGLE's copy, `-O2`), a 6.2 MB corpus (this repository's Markdown twice around the photograph's 4 MB of RGBA pixels): gzip `deflate` at levels 1, 6 and 9, `inflate` of each four times, `crc32` and `adler32` over the corpus 20 times, seconds per phase. The SIMD build has the CMake build's defines and flags in every mode (`INFLATE_CHUNK_SIMD_SSE2`, `ADLER32_SIMD_SSSE3`, `INFLATE_CHUNK_READ_64LE`, `DEFLATE_CHUNK_WRITE_64LE`, `DEFLATE_SLIDE_HASH_SSE2`, `CRC32_SIMD_SSE42_PCLMUL`, `-msse4.2 -mpclmul`); natively and in system mode its CPUID check selects the paths, and in portable mode a stub check reports SSE2 to SSE4.2 and PCLMUL, which code compiled for them always has there. Compressed output and checksums are identical to native's in every build.

| engine | deflate | inflate | crc32 | adler32 |
| --- | ---: | ---: | ---: | ---: |
| native, SIMD | 0.251 | 0.123 | 0.006 | 0.004 |
| native, plain | 0.257 | 0.173 | 0.016 | 0.024 |
| JIT, portable SIMD | 0.436 | 0.174 | 0.007 | 0.006 |
| JIT, portable plain | 0.587 | 0.213 | 0.019 | 0.026 |
| JIT, system SIMD (with PCLMUL) | 0.456 | 0.174 | 0.007 | 0.006 |
| JIT, system plain | 0.601 | 0.211 | 0.017 | 0.026 |
| NativeAOT, portable SIMD | 0.295 | 0.154 | 0.015 | 0.004 |
| NativeAOT, portable plain | 0.478 | 0.198 | 0.022 | 0.024 |

The SIMD paths make llvmnet's deflate 1.32 (system mode) to 1.62 (NativeAOT) times faster, inflate 1.21 to 1.29, adler32 4.3 to 6 and crc32 1.5 (NativeAOT, where PCLMULQDQ is an opportunistic set) to 2.7 times. Natively deflate gains little because Clang vectorizes the plain build's hash sliding at `-O2`, which llvmnet's default does not (ADR-11, SV-05).

**Abseil's swiss tables** (ANGLE's abseil-cpp, `-O2`): two million inserts, 16 million lookups and erases on integer keys, 500,000 inserts and four million lookups on string keys, through `flat_hash_map` and `flat_hash_set` with the SSE2 group (and SSSE3's `psignb`; `-mssse3`, in portable mode an alias of `--isa=x86-64-v2`) and with the portable group (`-U__SSE2__ -U__SSSE3__`). Every result is identical to native's.

| engine | SSE group | portable group |
| --- | ---: | ---: |
| native | 0.872 | 0.862 |
| JIT, portable | 2.154 | 2.093 |
| JIT, system mode | 2.092 | 1.977 |

The SSE group gains nothing natively on this workload and costs 3 to 6% under llvmnet (nine runs for the llvmnet rows).

## NativeAOT instruction sets

ILC's default instruction set on x64 is SSE2 with SSSE3 to SSE4.2 as opportunistic sets. The helpers test those at run time, but the `Vector128` operations the typed lowering emits for the same code's generic IR are compiled for SSE2: 32-bit multiplies, minimums and maximums, lane inserts and extracts become SSE2 sequences, as SV-05 found for `perlin`. astc-encoder's SSE4.1 build runs 4.41 s at the baseline against 2.42 s at `x86-64-v2`. A program compiled with `-march=x86-64-v2` requires an SSE4.2 processor natively, so the driver gives a NativeAOT program whose image records `x86-64-v2` ILC's `x86-64-v2` instruction set unless `--aot-instruction-set` says otherwise ([proposal](../plan/decisions.md#proposed-for-the-owner-2026-11-04-sv-08)).

## What it found besides

- **CPU detection in portable mode.** `--isa=x86-64-v2` defines `__x86_64__`, so libraries compile their CPUID-based detection, which is inline assembly that portable mode rejects: wuffs (astc-encoder's PNG decoder), astc-encoder's command-line check and Chromium zlib's `cpu_features.c`. The builds above use the libraries' switches (`WUFFS_CONFIG__AVOID_CPU_ARCH`) or replace the check; SV-12's CPUID emulation is the fix. Abseil's swiss tables and astc-encoder's core select their paths by the macros alone and need nothing.
- **System mode's CPUID recognizer** knew LLVM's and Eigen's spellings but not Clang 22's own `<cpuid.h>` (`xchg{q|} {%%|}rbx,%q1; cpuid`, with one or two inputs), which wuffs and zlib use. It now does, and `tests/c/cpu-query.c` compares `__cpuid` and `__cpuid_count` with the other forms.
- **Abseil's swiss tables** are 3 to 6% slower with their SSE2 group than with the portable one under llvmnet (2.15 against 2.09 s under the JIT, nine runs), and level natively; not investigated.
