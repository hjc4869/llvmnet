# Evidence: SIMD survey for the multi-level plan (2026-11-03)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (Zen 5: AVX2, FMA, BMI2, AVX-512), .NET SDK 10.0.403 with runtime 10.0.14 (the 11.0 release candidates are installed but were not used), NativeAOT ILCompiler 10.0.14, LLVM/Clang 22.1.8, commit `8a66389`. Everything measured here ran on the build host; the ARM64 and the other operating systems' behavior is taken from .NET's documented ISA support where stated, not measured.

The questions, from the owner's request for a multi-level SIMD plan ([12-simd.md](../plan/12-simd.md), [ADR-16](../plan/decisions.md#adr-16-simd-levels)): what forms SIMD takes in the software llvmnet is meant to build; what of it LLVM keeps generic and what it keeps target-specific; what LLVM's vectorizers emit for llvmnet's targets and what LLVM's own passes can do to legalize it; what .NET 10 offers under the JIT and NativeAOT, including the instruction-set behavior of NativeAOT; what emscripten and SIMDe do for the same problem; and whether hand-written `Vector128` code, the stand-in for a vector lowering, gains on scalar CIL what native vectorization gains on native scalar code. The scratch programs and the timing report are in the gitignored `artifacts/simd-survey/` (`probes/` for the census script, the intrinsic classification, the IR samples and the two .NET probes; `autovec/` for the kernels, the C# loops, the timing scripts and `REPORT.md` with every per-kernel table); each section gives the command that reproduces it.

## SIMD and assembly census of the survey corpus

`census.sh` counts, per source tree of the [library survey](2026-11-02-library-survey.md) (the `LightPlayer-Packages` checkouts and the AvaloniaStatic Skia and ANGLE trees), the C and C++ files, the assembly files (`.S`/`.s` and nasm `.asm`), and the files that include x86 intrinsic headers, `<arm_neon.h>`, `<wasm_simd128.h>` or `<arm_sve.h>`, contain inline assembly (`asm` or `__asm__` with parentheses), use GCC vector extensions (`vector_size`, `ext_vector_type`), detect the CPU (`__builtin_cpu_supports`, `cpuid`, `getauxval`, `AT_HWCAP`, `sysctlbyname`, `IsProcessorFeaturePresent`) or use `target`/`target_clones` attributes.

| Tree | C/C++ files | `.S` | `.asm` | x86 intrinsics | NEON | wasm | inline asm | vector ext. | CPU detection | `target` attr. |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| ffmpeg | 3,414 | 232 | 186 | 5 | 2 | 2 | 110 | 0 | 15 | 0 |
| dav1d | 83 | 65 | 48 | 1 | 0 | 0 | 1 | 0 | 7 | 0 |
| libjpeg-turbo | 248 | 3 | 91 | 1 | 15 | 0 | 7 | 0 | 1 | 0 |
| libjxl | 502 | 0 | 0 | 17 | 7 | 1 | 14 | 7 | 4 | 5 |
| Opus | 261 | 1 | 0 | 5 | 12 | 0 | 15 | 1 | 2 | 0 |
| zlib | 48 | 1 | 0 | 0 | 0 | 0 | 1 | 1 | 1 | 0 |
| libxml2, lcms2, LibRaw, jxrlib | 290 | 0 | 0 | 1 | 0 | 0 | 0 | 0 | 3 | 0 |
| Skia `src/` | 1,215 | 0 | 0 | 9 | 8 | 1 | 2 | 2 | 1 | 1 |
| HarfBuzz, FreeType, expat | 523 | 0 | 0 | 1 | 1 | 0 | 3 | 1 | 0 | 0 |
| libwebp | 171 | 0 | 0 | 4 | 7 | 0 | 22 | 0 | 1 | 0 |
| libpng, brotli, wuffs, Skia's zlib | 195 | 1 | 0 | 8 | 6 | 0 | 6 | 0 | 7 | 2 |
| ANGLE `src/` | 1,202 | 0 | 0 | 0 | 0 | 0 | 5 | 0 | 2 | 0 |
| ANGLE `third_party/` | 17,429 | 52 | 622 | 66 | 38 | 0 | 179 | 57 | 58 | 9 |

By architecture, the assembly is: ffmpeg x86 185 nasm files (plus 143 C files of dispatch and inline-assembly paths), AArch64 66 `.S`, ARM 78, RISC-V 73, LoongArch 14, PowerPC 1, WebAssembly 4 C files; dav1d x86 47 nasm, ARM 43 `.S`, RISC-V 12, LoongArch 10; libjpeg-turbo 91 nasm files for i386 and x86-64 and 22 C files of NEON intrinsics for ARM. Skia's SIMD is intrinsics and its `skvx` vector-extension library; libjxl's is Highway (intrinsics per target behind `target` attributes); Opus, libwebp and brotli use x86 and NEON intrinsics with C fallbacks. Every library with assembly also has C fallbacks selected by its own CPU detection, which in ffmpeg (`ff_get_cpu_flags_x86`) is CPUID and XGETBV inline assembly, in dav1d assembly (`dav1d_cpu_cpuid` in `cpu.asm`), in libpng and zlib `__builtin_cpu_supports` or CPUID, and on ARM `getauxval(AT_HWCAP)`.

The build host has `llvm-mc-22` and Clang (which cross-assembles `.S` for every registered target) but neither `nasm` nor lld (`ld.lld-22`, `lld-link-22`, `ld64.lld-22`), so Level 3's package helper (SV-14) needs both installed.

## Which intrinsics Clang lowers to generic IR

`sse.c` calls 99 common SSE, SSE2, SSE3, SSSE3, SSE4.1, SSE4.2, AVX, AVX2 and FMA intrinsics, one per function; `clang-22 -O1 -mavx2 -mfma -msse4.2 -S -emit-llvm sse.c` shows which of them become generic vector IR and which call an `llvm.x86.*` target intrinsic.

- **Generic IR (53):** element-wise arithmetic, logic and shifts by an immediate, saturating add and subtract (`llvm.sadd.sat`, `llvm.usub.sat`), integer min, max and abs, `mullo`, `mul_epu32`, `mul_epi32`, compares, blends with an immediate, `unpack`, `shuffle_epi32`, `shufflelo`, `shuffle_ps`, `movehl`, `alignr`, `srli_si128`, insert and extract, `movemask` (a `bitcast` of the compare mask), `cvtepu8`/`cvtepi16` widenings, `cvtepi32_ps`, `cvtsi128_si32`, `sqrt`, `fmadd` (`llvm.fma`), `round_ps` with the nearest mode, and AVX2's 256-bit forms of the same (`add`, `abs`, `max_epu8`, `cvtepu8_epi16`, `unpacklo`, `alignr`, `permute2x128`, `movemask`).
- **Target intrinsics (46):** `packs`/`packus` (`sse2.packsswb.128`, `packssdw.128`, `packuswb.128`, `sse41.packusdw`, `avx2.packuswb`), `psadbw`, `pmaddwd`, `pmaddubsw`, `pmulhw`, `pmulhuw`, `pmulhrsw`, `pavgb`, `pshufb`, `phaddw`, `phaddd`, `psignw`, shifts by a vector count (`psll.w`, `psra.d`, `avx2.psllv.d.256`), `blendvps`/`pblendvb`, `ptestz`, `dpps`, `phminposuw`, `mpsadbw`, `cvtps2dq`, `cvttps2dq`, `cvtpd2dq`, `cvtss2si`, `cvttss2si`, `comilt.ss`, `min.ps`/`max.ps`, `rcp.ps`, `rsqrt.ps`, `hadd.ps`, `floor_ps` (`sse41.round.ps`), `crc32`, `avx.rcp.ps.256`, `avx2.gather.d.d.256`, `avx2.permd`, `avx2.pshuf.b`, `avx2.pmadd.wd`, `avx2.pmul.hr.sw`, `avx2.psad.bw`, `avx2.pblendvb`, `avx2.phadd.d`.

LLVM 22's intrinsic tables (`llvm/IR/Intrinsics*.td`) define 1,687 x86 intrinsics: MMX 70, SSE 37, SSE2 60, SSE3 9, SSSE3 27, SSE4.1 16, SSE4.2 18, AVX 56, AVX2 101, FMA 4, AVX-512 723, AVX10 245, XOP 46, AES 22, BMI 8, SHA 7, PCLMUL 3 and others; 1,417 AArch64 intrinsics, of which 209 NEON and 886 SVE; 74 WebAssembly intrinsics, of which 25 are SIMD (`swizzle`, `shuffle`, `avgr_unsigned`, `bitselect`, `anytrue`, `alltrue`, `bitmask`, `dot`, `narrow_signed`/`unsigned`, `q15mulr_sat_signed`, `pmin`, `pmax`, `extadd_pairwise_*`, the relaxed forms, and `f16x8` lanes). Clang's own headers: `emmintrin.h` defines 328 inline functions of which 98 use an `__builtin_ia32_*` builtin, `avx2intrin.h` 282 of which 111, `arm_neon.h` 5,217 inline functions, with 5,388 lines referencing `__builtin_neon_*` builtins (which Clang's CodeGen lowers to generic IR or to `llvm.aarch64.neon.*`), `wasm_simd128.h` 354 of which 92 use a `__builtin_wasm_*`.

## What the vectorizers emit

`bin/llvmnet --runtime=portable -O3 -fvectorize -fslp-vectorize -S -emit-llvm tests/c/vector-loops.c` (the portable target, x86-64 with SSE2 features) and `--runtime=system -O3 -march=x86-64-v3 ...` (AVX2):

| Target | Vector types (occurrences) | Vector intrinsics | `shufflevector` / `extractelement` / `insertelement` |
| --- | --- | --- | --- |
| portable (SSE2 model) | `<4 x i32>` 78, `<2 x i32>` 40, `<2 x float>` 17, `<2 x double>` 11, `<4 x i1>` 8, `<3 x i32>` 6, `<2 x i1>` 1 | `llvm.fmuladd.v2f32` 2 (the rest scalar `fmuladd`, `smin`, `smax`) | 15 / 8 / 0 |
| system, `-march=x86-64-v3` (AVX2 model) | `<4 x i32>` 161, `<8 x i32>` 102, `<4 x float>` 35, `<16 x i32>` 34, `<4 x i64>` 32, `<4 x double>` 28, `<2 x i32>` 26, `<4 x i8>` 10, `<8 x i1>` 5, `<4 x i16>` 4 | `llvm.masked.store.v8i32` 5, `llvm.smin/smax.v4i32` 6 each, `llvm.fmuladd.v4f32` 3, `llvm.vector.reduce.{smin,smax,mul,add}` 2 each | 11 / 0 / 0 |

The SSE2 model keeps every vector at or under 128 bits and the SLP vectorizer adds 64-bit (`<2 x i32>`, `<2 x float>`) and 96-bit (`<3 x i32>`) vectors, which is why Level 1 widens sub-128-bit vectors rather than mapping them to `Vector64<T>`. The AVX2 model doubles the widths, uses masked stores for the if-converted loop and reductions for the loop-carried minimum, maximum, product and sum. `__attribute__((target("avx2")))` on one function of a portable build already makes the vectorizer emit `<8 x i32>` for that function (29 occurrences against 17 `<4 x i32>` in `tgt.c`), which today scalarizes.

`__builtin_cpu_init()` and `__builtin_cpu_supports("avx2")` compile for the portable target to loads from `@__cpu_model` and fail at the link in both modes ("Unresolved external global: __cpu_model" in portable mode, because `libclang_rt.builtins.a` in the sysroot has no `cpu_model` member; "Unresolved system ABI symbol __cpu_model" in system mode). `target_clones("avx2","default")` produces an `ifunc` with a resolver reading `__cpu_model`, which the backend rejects by design today.

## Legalizing wide vectors with LLVM's scalarizer

`opt-22 -passes='function(scalarizer<min-bits=128>)'` on `frag.ll` (an `add <8 x i32>`, a `llvm.fmuladd.v16f32`, an `add <4 x i32>`, a two-source `shufflevector <8 x i32>` and a `llvm.vector.reduce.add.v8i32`):

- `add <8 x i32>` becomes two `add <4 x i32>` on halves extracted with `shufflevector` and reassembled with two more; `fmuladd.v16f32` becomes four `llvm.fmuladd.v4f32`; `add <4 x i32>` is untouched.
- The `<8 x i32>` shuffle and the reduction are left whole: the scalarizer fragments only element-wise operations. With `expand-reductions` first, the reduction becomes a shuffle tree whose adds are then fragmented, with the half-extraction shuffles between them.

So the pass is the legalizer Level 1 needs for arithmetic, and the backend has to split shuffles, reductions, loads and stores of wide vectors itself (or lower reductions directly to `Vector128.Sum` and the like), which [12-simd.md](../plan/12-simd.md#level-1-general-vector-lowering) assigns to SV-02 and SV-03.

## .NET SIMD surface and NativeAOT instruction sets

A reflection probe over `System.Private.CoreLib` of .NET 10.0.14 on the build host (`isaprobe`):

- `Vector<T>` is 256 bits; `Vector128`, `Vector256` and `Vector512` report hardware acceleration under the JIT, `Vector64` does not (x64 has no 64-bit SIMD path). `Vector128` has 318 public static methods: arithmetic, comparisons (`Equals`, `GreaterThan`, ... and the `All`/`Any` forms), `ConditionalSelect`, `Shuffle` and `ShuffleNative` (single-source only; a two-source shuffle is two shuffles and a select, as [Simd128.cs](../../src/LlvmNet.Runtime/Simd128.cs) does), `Widen`/`Narrow`/`NarrowWithSaturation`, `AddSaturate`/`SubtractSaturate`, saturating and `Native` conversions, `Sum`, `Dot`, `ExtractMostSignificantBits`, `Load`/`Store` with aligned and unaligned forms, `Min`/`Max` with the `Number`, `Magnitude` and `Native` variants, `MultiplyAddEstimate`, `FusedMultiplyAdd`, rounding, and the transcendental set.
- ISA classes and their method counts: x86 `Sse` 87, `Sse2` 299, `Sse3` 18, `Ssse3` 24, `Sse41` 139, `Sse42` 4, `Avx` 243, `Avx2` 393, `Fma` 32, `Bmi1`/`Bmi2`/`Lzcnt`/`Popcnt`, `Aes`, `Pclmulqdq`, `Gfni`, `AvxVnni`, `Avx512F` 571 (+405 `VL`), `Avx512BW` 144, `Avx512CD`, `Avx512DQ`, `Avx512Vbmi`, `Avx512Vbmi2`, `Avx10v1` 699, `Avx10v2` 20, `X86Base` (with `CpuId`); ARM `AdvSimd` 1,771 (+857 `Arm64`), `Dp`, `Rdm`, `Crc32`, `Aes`, `Sha1`, `Sha256`, `ArmBase`, `Sve` 1,538 and `Sve2` 647 (present, not usable at run time in .NET 10); WebAssembly `PackedSimd` 470. On the host every x86 class through AVX-512 reports supported except AVX10 and the VNNI-int variants.

`aotisa`, a program that prints `IsSupported` values and times a few operations in `NoInlining` methods (100 million iterations, nanoseconds per call including the call):

| Build | SSSE3, SSE4.1, SSE4.2, POPCNT | AVX, AVX2, FMA, BMI2 | AVX-512 | `Vector256` accel. | `Vector<T>` | `Vector128.Shuffle` (run-time indices) | explicit `Ssse3.IsSupported` path | `Vector256<int>` add | `Vector128<int>` add | `Vector64<int>` add | `Vector128<long>` multiply |
| --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| NativeAOT, default | yes | no | no | no | 128 | 13.2 | 4.2 | 4.3 | 4.3 | 7.8 | 6.1 |
| NativeAOT, `IlcInstructionSet=x86-64-v3` | yes | yes | no | yes | 256 | 6.3 | 5.7 | 6.3 | 6.2 | 10.6 | 8.8 |
| JIT | yes | yes | yes | yes | 256 | 6.3 | 5.7 | 6.7 | 6.2 | 10.0 | 4.5 |
| JIT, `DOTNET_EnableAVX=0` | yes | no | no | no | 128 | 4.3 | 3.9 | 5.5 | 4.4 | 9.1 | 6.1 |

What this shows: ILC's default level makes SSSE3 to SSE4.2 and POPCNT *opportunistic* (their `IsSupported` is a runtime check that reads a field the runtime fills at startup) and does not enable AVX; `x86-64-v3` adds AVX2, FMA and BMI2 and makes `Vector256` accelerated; AVX-512 is never opportunistic. An opportunistic set helps only IL that branches on `IsSupported` itself: the generic `Vector128.Shuffle` with run-time indices is compiled against the guaranteed set (SSE2, no `pshufb`) and takes 13 ns, while a method that tests `Ssse3.IsSupported` and calls `Ssse3.Shuffle` takes 4 ns in the same executable. This is the reason Level 2's helpers dispatch explicitly instead of relying on the `Vector128` API, and it explains the PF-08 finding that the SIMD128 profile is slower than scalar under baseline NativeAOT. `Vector64` operations cost about twice their `Vector128` equivalents on x64, so Level 1 widens 64-bit vectors; a `Vector256` operation without AVX (the halves fallback .NET uses, and what an ARM64 host would do) costs about 1.3 times a `Vector128` operation, so keeping 256-bit vectors is safe where a build asks for them; `Vector128<long>` multiplication has no SSE2 instruction and is composed (6 ns against 4.5 ns with AVX-512's `vpmullq`).

ILC's instruction-set groups (`ilc --help`): x64 `x86-64`, `x86-64-v2`, `x86-64-v3`, `x86-64-v4` and the individual sets; arm64 `armv8-a` to `armv8.6-a`, `apple-m1`, with `neon`, `dotprod`, `sve`, `sve2`. NativeAOT links native archives with `<NativeLibrary Include="x.a">` and binds P/Invokes statically with `<DirectPInvoke Include="module">`, which [Toolchain.cs](../../src/LlvmNet.Compiler/Toolchain.cs) already uses for the system-mode bridge archive; the same items carry a kernel package's archive, conditioned on the runtime identifier.

## Emscripten and SIMDe

Emscripten ([porting/simd](https://emscripten.org/docs/porting/simd.html)) enables WebAssembly SIMD and LLVM's vectorizers together with `-msimd128`; offers five routes (autovectorization, GCC vector extensions, `wasm_simd128.h`, x86 intrinsics through `xmmintrin.h` to `immintrin.h` behind `-msse` to `-mavx2`/`-mfma`, NEON through a SIMDe-derived `arm_neon.h` behind `-mfpu=neon`); emulates 256-bit AVX operations as two 128-bit ones and scalarizes gathers, variable shifts and masked stores; documents per intrinsic whether it is a native opcode, a few instructions, or a slow emulation (`_mm_sad_epu8` is eleven instructions, `minps`/`maxps` seven to ten, `trunc_sat` five to fourteen), and prints warnings for the slow ones under `WASM_SIMD_COMPAT_SLOW`; makes `_mm_pause`, prefetch and `clflush` no-ops, returns fixed values from `_MM_GET_ROUNDING_MODE`, and refuses `_mm_setcsr` and the MMX intrinsics; supports no inline or file assembly. SIMDe ([simd-everywhere/simde](https://github.com/simd-everywhere/simde), MIT) implements MMX through AVX2, FMA, parts of AVX-512, NEON, WebAssembly SIMD and parts of SVE as header-only C, over other ISAs' intrinsics, Clang's generic builtins (`__builtin_shufflevector`, `__builtin_convertvector`) and GCC vector extensions, with `SIMDE_ENABLE_NATIVE_ALIASES` for the native names. llvmnet's Level 2 is emscripten's compatibility headers moved into the runtime (one table for the C intrinsics and for the `llvm.x86.*` calls in existing bitcode, with the host instruction where it exists), and SV-11's NEON support is SIMDe's `arm_neon.h` over Level 1, as emscripten's is over WebAssembly SIMD.

## Hand-written vectors against scalar and native code

`artifacts/simd-survey/autovec/` measures, for seven kernels on arrays of 4,096 elements repeated so that a native scalar run takes 0.3 to 0.7 s (`kernels.c`: `saxpy`, `i32mix` = `a*3 + (b>>2) - (a&b)`, `sad8` = sum of byte absolute differences, `dot16` = widening 16-bit dot product, `max8` = byte maximum, `fdot` = float dot product with 4 partial sums, `fdot8` with 8, and `paeth` = libpng's Paeth filter at 4 bytes per pixel as a control), six C builds (clang-22 `-O2 -fno-vectorize`, `-O3 -fvectorize -fslp-vectorize` with SSE2, the same with `-march=x86-64-v3`; llvmnet portable `-O2` under the JIT, `-O3 -fvectorize -fslp-vectorize` under the JIT with the vector IR scalarized as today, and `--nativeaot -O2`), and a C# program (`cs/Kernels.cs`) with a scalar loop, a `Vector128<T>` loop shaped like LLVM's SSE2 output for the kernel (the stand-in for Level 1) and a `Vector256<T>` loop, under the JIT, NativeAOT at the default instruction set and NativeAOT with `IlcInstructionSet=x86-64-v3`; `sad8` and `dot16` also have `Sse2.SumAbsoluteDifferences`/`MultiplyAddAdjacent` and the AVX2 variants. All C builds use `-ffp-contract=off` so that no variant fuses. `scripts/ab-time.py 7` timed every variant of a kernel alternately; the ratios below use medians net of each runtime's startup floor (61 ms for an llvmnet portable image, 20 ms for a C# program under the JIT, 2 to 3 ms as NativeAOT). Every variant of every kernel printed the same checksum (15 variants per kernel, 20 for `sad8` and `dot16`); the AVX2 intrinsic variants fail under the default NativeAOT instruction set with `PlatformNotSupportedException`, as expected.

| Kernel | llvmnet `-O2` JIT / native scalar | llvmnet `-O3` vectorized and scalarized / `-O2` | gain of a `Vector128` lowering over llvmnet `-O2` (JIT) | gain of `Vector256` (JIT) | C# `Vector128` / native SSE2 | C# `Vector256` / native AVX2 | x86 intrinsic idiom / native |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| saxpy | 1.46 | 0.98 | 5.0x | 9.7x | 0.94 | 0.90 | |
| i32mix | 1.18 | 2.02 | 4.8x | 9.8x | 0.76 | 0.92 | |
| sad8 | 0.94 | 1.56 | 3.7x | 7.2x | 1.37 | 1.58 | 0.22 (`psadbw`), 0.21 (AVX2) |
| dot16 | 1.52 | 1.33 | 5.0x | 9.5x | 1.36 | 1.48 | 0.44 (`pmaddwd`), 0.45 (AVX2) |
| max8 | 7.23 | 0.82 | 100x | 171x | 0.92 | 1.40 | |
| fdot | 1.09 | 1.02 | 2.3x | (4 chains fill 4 lanes) | 1.00 | | |
| fdot8 | 1.04 | 1.48 | 3.9x | 4.4x | 1.01 | 1.02 | |
| paeth | 1.31 | 1.22 | (control) | | | | |

Native vectorization gains 2.2 to 5.4x with SSE2 and 2.2 to 12x with AVX2 on the same kernels (12.7x and 33x on `max8`); the `Vector128` loops gain 2.1 to 5.3x over the C# scalar loops under the JIT and the `Vector256` loops 4.2 to 10.2x, the same ratios as native. Under NativeAOT's default instruction set the `Vector128` gains are 2.3 to 5.1x and `Vector256` runs as two halves (level with `Vector128`); with `x86-64-v3` both match the JIT.

What the numbers say:

- **A `Vector128` lowering of LLVM's own vector shapes gains 3.7 to 5x on element-wise and widening loops and 2.3 to 3.9x on float dot products, against today's scalar translation; `Vector256` about doubles that on a host with AVX2.** The C# loops keep their pointers in locals, while translated code reloads each global's base address from its static field on every access (a store through a pointer may alias the field; visible in the JIT and AOT disassembly of `k_saxpy`), so the gains are an upper bound until the lowering or the scalar backend hoists those loads (PF-17).
- **The neutral `Vector128` API is level with native SSE2 where an instruction exists:** 0.94x (saxpy), 0.76x (i32mix, where the JIT uses `pmulld` and VEX against clang's two `paddd`), 0.92x (max8), 1.00 and 1.01x (the dot products); `Vector256` is at 0.90 to 1.04x of native AVX2. `Abs`, `Max`, `Widen`, `ShiftRightArithmetic` and `Sum` map one to one onto `llvm.abs`, `umax`, `zext`/`sext`, `ashr` and the reduction epilogues.
- **The widening reductions are the gap:** LLVM's x86 backend pattern-matches `sad8`'s widen-subtract-abs-add and `dot16`'s widen-multiply-add into `psadbw` and `pmaddwd`; the neutral API executes the dataflow literally and runs 1.36 to 1.58x slower than native. `Sse2.SumAbsoluteDifferences` and `Sse2.MultiplyAddAdjacent` behind `IsSupported` checks close the gap and beat clang's own shape (0.21 to 0.45x of native time, because clang's cost model chose 8-byte `psadbw` operands and half-filled `pmaddwd`), which is why Level 1 should recognize these idioms as the backend does (SV-21).
- **Today's scalarized vectorization is a loss:** `-O3 -fvectorize` costs 2.0x (i32mix), 1.56x (sad8), 1.48x (fdot8), 1.33x (dot16) and 1.22x (paeth) over `-O2`, is neutral on saxpy and fdot, and gains 0.82x on max8 only because the scalarized `<16 x i8>` maximum becomes 16 independent chains. ADR-11's scalar default is right until a lowering exists.
- **Under NativeAOT's default instruction set, `Vector128<int>` multiplication is composed** from `pmuludq`, `pshufd`, `psrldq` and `punpckldq` (the `*3` in i32mix and the widened multiply in dot16 run 2x slower than under the JIT, although `Sse41.IsSupported` is true at run time: the generic API again), and `--aot-instruction-set x86-64-v3` removes it, consistent with PF-08.
- **The scalar baseline has its own finding:** llvmnet's scalar `max8` is 7.2x native, because the branch-free mask select of PF-15 turns the loop-carried `m = a[i] > m ? a[i] : m` into a dependency chain of about five cycles per element, where native clang keeps a perfectly predicted branch (0.65 cycles per element); a loop-carried min or max is a case PF-15's rule should exclude (PF-18). The scalar translation is otherwise at 0.94 to 1.52x native. llvmnet's NativeAOT `saxpy` is slower than its JIT (1.14 against 0.85 s net) because without VEX the two-operand `mulss` clobbers the multiplier and the loop reloads it every iteration.
- **Sub-128-bit vectors are common in LLVM's x86 output:** `sad8`, `dot16` and `paeth` vectorize as `<4 x i8>`, `<4 x i16>` and `<4 x i32>` under the SSE2 cost model, so the lowering's choice between padded `Vector128` lanes and scalarization for them must be measured; `paeth` with 4 bytes per pixel has four independent lanes and is a weaker control than intended (native AVX2 vectorizes it, 1.38x).

## What the survey shows

- The ecosystem's SIMD is in every form the plan covers, and assembly dominates in the codecs: ffmpeg, dav1d and libjpeg-turbo carry hundreds of nasm and `.S` files with C fallbacks and their own CPU detection, which llvmnet cannot translate but can call, as Level 3 proposes; intrinsics and vector extensions (Skia, libjxl, Opus, libwebp, brotli, zlib) are what Levels 1 and 2 serve.
- About half of the common x86 intrinsics are generic IR after Clang, so Level 1 alone makes them vector code; the other half (packs, multiply-add, averages, byte shuffles, horizontal operations, the conversions and NaN-propagating minimum and maximum) is a bounded table of 328 definitions from SSE to AVX2 and FMA, against which Level 2's helpers can be checked one by one.
- LLVM's vectorizers produce exactly the 128-bit and sub-128-bit shapes the proposed mapping expects for the portable target, and `scalarizer<min-bits=128>` legalizes wider arithmetic for free.
- .NET 10 has every operation the mapping needs and the ISA classes for the fast paths; the one trap is NativeAOT's opportunistic sets, which only explicit `IsSupported` checks benefit from.
- CPU detection is the gap that would make Level 3 unusable: `__cpu_model` does not link, `ifunc` is rejected, and portable mode has no CPUID; all three have small runtime fixes (SV-12).

## What it does not show

- No vector lowering exists; the timing section uses hand-written C# as a stand-in for what the backend would emit, on seven small kernels with the arrays in cache, and a real lowering will be worse until its shuffles, epilogues and base-address loads are tuned. Real programs (the benchmark set, the survey's libraries) are SV-05's measurement.
- Nothing ran on ARM64, Windows or macOS; NEON costs, the `Vector256` halves fallback on ARM64 and the Windows calling convention for vector arguments are taken from .NET's documentation.
- No kernel package was built: nasm and lld are not installed on the build host, so the Level 3 flow (assemble per identifier, generate stubs, load by identifier, link statically) is designed from the NX spike's mechanism and NativeAOT's documented items, not demonstrated.
- The helper table's exactness (NaN, saturation and rounding rules per intrinsic) is asserted from the instruction definitions; the conformance test that would prove it is SV-08.
