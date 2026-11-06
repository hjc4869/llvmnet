# SIMD and vector code

C, C++ and Fortran reach the processor's SIMD units in four ways: LLVM's vectorizers, Clang's vector extensions (GCC's `vector_size`, as Skia's `skvx` and SIMDe write them), CPU intrinsics (`<immintrin.h>`, `<wasm_simd128.h>`) and assembly. llvmnet gives each of them a path to the host's SIMD instructions while a portable image stays one file for every host: the JIT and NativeAOT pick the instructions of the processor the image runs on ([ADR-16](../plan/decisions.md#adr-16-simd-levels)). This page says what happens to each form, which options choose what, and what to expect per host and mode.

| Code | What llvmnet does | Options | More |
| --- | --- | --- | --- |
| Loops the vectorizers vectorize (`-O2` and above, as in Clang) and Clang's vector extensions | `Vector128<T>` code (or `Vector256<T>`), Level 1 | `-O2`, `-fno-vectorize`, `--vector-width` | [below](#vector-code) |
| SSE to SSE4.2, PCLMULQDQ | the host's x86 instruction, ARM64's of the same meaning, or an exact composition, Level 2 | `--isa=x86-64-v2` or `-msse4.2` and the other flags of the level | [x86-intrinsics.md](x86-intrinsics.md) |
| AVX, AVX2, FMA, F16C, BMI | the same | `--isa=x86-64-v3`, `-mavx2` and the others | [x86-intrinsics.md](x86-intrinsics.md) |
| AVX-512, AES, SHA | not translated: a warning when compiling, an error when linking a program that reaches them | build the code's AVX2, SSE or C path | [x86-intrinsics.md](x86-intrinsics.md#what-is-not-translated) |
| `wasm_simd128.h` | `Vector128<T>` code with a few helpers | `--isa=wasm-simd128` (`--simd128`) | [simd128.md](simd128.md) |
| `arm_neon.h` | not yet (SV-11): the portable target is x86-64's data layout, which Clang's NEON header refuses | build the library's x86 or C path | |
| `.asm`, `.S` | portable mode: native kernel packages per runtime identifier; system mode: linked into the image's native library | `--native-kernels`; system mode takes the files as inputs | [native-kernels.md](native-kernels.md), [linking.md](linking.md#native-objects-and-assembly-system-mode) |
| Inline assembly | rejected, except CPUID, XGETBV and a few no-ops | the project's switch (`--disable-inline-asm`) | [modes.md](modes.md#system-abi) |
| CPU detection (CPUID, `__builtin_cpu_supports`, `target_clones`, `getauxval`) | answers with the host's features as .NET runs them | | [x86-intrinsics.md](x86-intrinsics.md#cpu-detection) |

A program may mix them; a library that targets one instruction set per compile, as most do with `#ifdef __SSE4_1__` or `#ifdef HAVE_X86ASM`, builds unchanged with the level it targets.

## Vector code

LLVM vector IR of 8- to 64-bit integer, pointer, `float` and `double` lanes, and its masks, becomes typed `Vector128<T>` operations, which RyuJIT and NativeAOT compile to SSE, AVX or NEON instructions ([architecture.md](../dev/architecture.md#values-and-control-flow)). A vector of at most 128 bits is one `Vector128<T>`; a wider one is several. LLVM's x86-64 cost model chooses the vector widths, as it does natively for SSE2, so the loop vectorizer emits `<4 x i32>`, `<4 x float>`, `<2 x double>` and 64-bit vectors such as `<4 x i16>`.

- **Vectorizers.** `-O2`, `-O3`, `-Os` and `-Ofast` run the loop and SLP vectorizers, as Clang does; `-O1`, the default, does not, and `-fno-vectorize -fno-slp-vectorize` keep code scalar at any level. The sysroot's libraries are built scalar. Vectorized, the SIMD survey's kernels run 2 to 5 times faster than llvmnet's scalar builds, and the benchmark set gains about what native code gains (1.5% at `-O2`, 1.2 to 1.8 times on its numeric loops; [evidence](../evidence/2026-11-03-vector-lowering.md#sv-05-autovectorization)). `-O3` adds nothing that `-O2` does not.
- **256 bits.** `--vector-width=256` holds vectors in `Vector256<T>`, one instruction on a JIT host with AVX2 and as NativeAOT at `x86-64-v3`, 1.25 to 1.9 times faster than 128 bits there; elsewhere .NET computes `Vector256` as halves, 1.07 to 1.5 times slower than 128 on ARM64. It is meant for builds that target AVX2 hosts, and `--isa=x86-64-v3` implies it ([evidence](../evidence/2026-11-03-vector-lowering.md#sv-06-256-bit-fragments)).
- **Scalar functions.** A function whose vector code the lowering does not translate (lanes such as `i128` or `x86_fp80`, vector `getelementptr`, a vector intrinsic without a lowering), or whose vectors mostly carry values around rather than compute (the x86-64 ABI's pairs of floats, small arrays), runs as scalar code, as all vector code did before ADR-16. `-v` and `--report-simd` name them.
- **Sums of absolute differences and of products.** A loop that sums `abs(a[i] - b[i])` over bytes, or `a[i] * b[i]` over 16-bit values, runs as `psadbw` and `pmaddwd` where the host has them, as LLVM's x86 backend compiles it natively: ffmpeg's motion-estimation SADs and dav1d's 8-tap filters are such sums ([evidence](../evidence/2026-11-05-widening-reductions.md)). A rounding average of bytes or 16-bit values, `(a + b + 1) >> 1` as the vectorizers widen it, runs as `pavgb` or `pavgw` (`urhadd` on ARM64): pixel averages and half-pel interpolation ([evidence](../evidence/2026-11-06-x264.md#sv-28-rounding-averages-as-pavgb-and-pavgw)).
- **ARM64.** Where AdvSimd has an instruction of its own for a shape the vectorizers write, the lowering takes it on ARM64 and keeps x64's code: loads and stores of 8 and 4 bytes are one `ldr d` or `ldr s`, a row's sum of absolute differences is `uabd` and `uaddlv`, a narrowing to 64 bits one `xtn`, and a difference or sum of widened bytes or halfwords one `usubl` or `uaddl` ([evidence](../evidence/2026-11-07-arm64-lowering.md)).
- **Masked loads and stores** (`-mavx2` loops with tail masks, `llvm.masked.*`) run lane by lane, because a masked lane must not touch memory.

## Choosing a level

| `--isa` | Macros and intrinsics | Vectorizer cost model | NativeAOT instruction set on x64 |
| --- | --- | --- | --- |
| `baseline` (default) | none: libraries take their C paths | SSE2 | `x86-64-v2` for a program with vector code, else the baseline (SSE2, SSE4.2 tested at run time) |
| `x86-64-v2` | `__SSE__` to `__SSE4_2__`, `__POPCNT__`, `__x86_64__` | SSE4.2 | `x86-64-v2` |
| `x86-64-v3` | also `__AVX__`, `__AVX2__`, `__FMA__`, `__F16C__`, `__BMI__`, `__BMI2__`, `__LZCNT__` | AVX2, 256-bit vectors | `x86-64-v3` |
| `wasm-simd128` | `__LLVMNET_SIMD128__` and llvmnet's `wasm_simd128.h` | SSE2 | `x86-64-v2` |

The level changes no data layout: objects of every level link together, and the image is still one file for every host. A program compiled at `x86-64-v2` or `x86-64-v3` runs on ARM64 and on x64 processors without the level too, through the helpers' compositions, but a NativeAOT executable at that level requires it, as native code compiled with `-march` does; `--aot-instruction-set baseline` keeps SSE2 for such an executable, at a cost to the vector code around the intrinsics ([x86-intrinsics.md](x86-intrinsics.md#nativeaot)). In system mode the `-m` and `-march` options work as natively and the baseline predefines `__SSE2__`, as x86-64 does.

## What to expect per host

| Host and engine | `Vector128` | `Vector256` | x86 helpers (Level 2) |
| --- | --- | --- | --- |
| x64 with AVX2, JIT | instructions | instructions | the x86 instruction |
| x64, NativeAOT baseline | SSE2 instructions; SSE4.1 operations composed | halves | the x86 instruction, tested at run time up to SSE4.2; AVX composed |
| x64, NativeAOT `x86-64-v3` | instructions | instructions | the x86 instruction |
| x64 without AVX (or `DOTNET_EnableAVX=0`) | instructions | halves, slower than 128 | SSE instructions; AVX by halves or composed |
| ARM64 (macOS, Linux, Windows), JIT and NativeAOT | NEON instructions | halves | AdvSimd's instruction where one has the same meaning, else composed |
| `DOTNET_EnableHWIntrinsic=0` | software | software | composed |

A NativeAOT executable on x64 targets `x86-64-v2` (SSE4.2 and POPCNT, x64 processors since about 2009) when the program has vector code, a function the lowering keeps as vector code that computes on lanes, which is most programs built at `-O2` and above: at the baseline, ILC composes the SSE4.1 operations the vectorizers use from SSE2, and dav1d decoded 1.26 times slower than its own scalar build there, against 1.44 times faster at `x86-64-v2` ([evidence](../evidence/2026-11-03-vector-lowering.md#the-surveys-libraries-2026-11-05); the owner's decision of 2026-11-05). `-v` says when that applies, and `--aot-instruction-set baseline` gives an executable that starts on every x64 processor.

The results are the same everywhere, bit for bit, apart from the NaN payloads and approximations [x86-intrinsics.md](x86-intrinsics.md#exactness) lists. A few measured cases, each against the same library's scalar build with llvmnet:

| Code | Gain | Against native | Evidence |
| --- | --- | --- | --- |
| astc-encoder, SSE4.1 intrinsics | 2.8 times (JIT), 3.4 (NativeAOT) | 1.4 times native SSE4.1 | [SV-08](../evidence/2026-11-04-x86-intrinsics.md) |
| libjxl, Highway's AVX2 target | 4.4 times (NativeAOT, effort 7) | 2.1 times native AVX2 | [SV-09](../evidence/2026-11-04-avx2-intrinsics.md#libjxl) |
| ffmpeg's browser profile with its nasm kernels (a kernel package) | 1.6 to 1.9 times (NativeAOT) | 1.35 to 1.4 times native assembly | [SV-13](../evidence/2026-11-04-native-kernels.md) |
| ffmpeg's mpeg4 encoder, C SADs vectorized | 1.6 times (NativeAOT) | 1.6 times native vectorized | [SV-21](../evidence/2026-11-05-widening-reductions.md#ffmpegs-sad) |
| ffmpeg's SIMD128 HEVC profile | faster than scalar in every engine | | [SV-07](../evidence/2026-11-03-vector-lowering.md#sv-07-simd128-on-the-typed-lowering) |

Under the JIT a program starts slower and reaches its NativeAOT speed only once its hot code is compiled at Tier1: short runs, and long loops in a function called once, measure the JIT's tiering as much as the vector code.

## Seeing what vector code runs as

`--report-simd` prints, when an image is written, what its vector code became: the functions that run as vector code and the operations in them, the functions scalarized and why, the reductions run as `psadbw` and `pmaddwd` and the averages as `pavgb` and `pavgw`, and each SIMD helper the code calls with the instruction sets it uses on x64 and on ARM64 ("composed" where it builds the result from other operations), as emscripten's `WASM_SIMD_COMPAT_SLOW` warnings name the operations that are not one instruction:

```text
$ llvmnet --isa=x86-64-v2 -O2 --report-simd kernel.c -o kernel.dll
llvmnet: simd: 4 functions make 37 Vector128 operations and 11 SIMD helper calls; 0 functions with vector IR run as scalar code
llvmnet: simd: reductions of sad: 2 sums of absolute differences as psadbw
llvmnet: simd:   calls x64                    ARM64                  helper: functions
llvmnet: simd:       9 Sse42                  Crc32                  X86.Crc32b (llvm.x86.sse42.crc32.32.8): checksum
llvmnet: simd:       2 Sse2                   AdvSimd                X86.Psadbw (llvm.x86.sse2.psad.bw): sad
llvmnet: simd: operations  calls function
llvmnet: simd:         28      2 sad
llvmnet: simd:          0      9 checksum
llvmnet: simd:          7      0 main
llvmnet: simd:          2      0 reverse16
```

Here `kernel.c` has a vectorized loop that sums absolute differences (`sad`), a byte reversal with `_mm_shuffle_epi8` and a constant order, which Clang folds into a generic shuffle (`reverse16`, two `Vector128` operations), and a CRC-32C loop with `_mm_crc32_u8` (`checksum`). The helper table counts call sites, not executions; the instruction sets are those the helper tests with `IsSupported`, the first one that the host has runs. A helper composed on a host family, such as the SSE4.2 string comparisons (`_mm_cmpestri`) on x64, for which .NET has no intrinsic, computes the instruction's result from other operations there, which is correct but slower than the instruction. `-v` prints the scalarized functions and the recognized reductions as notes while linking.

## Assembly

llvmnet does not translate assembly. In portable mode a project assembles its kernels natively for each runtime identifier it wants to ship and makes a [native kernel package](native-kernels.md) of them, which the image calls through generated P/Invoke stubs: under the JIT from `runtimes/<rid>/native/` beside the image, as NativeAOT from the target's static library; on hosts the package does not cover, the program's CPU detection reports no features and its C paths run. In system mode `.asm`, `.S` and native objects are linked into the image's native library ([linking.md](linking.md#native-objects-and-assembly-system-mode)). ffmpeg's browser profile with its nasm kernels is the worked example of both.

## Options

| Option | Modes | Meaning |
| --- | --- | --- |
| `--isa=<level>` | both | `baseline`, `x86-64-v2`, `x86-64-v3` or `wasm-simd128`: intrinsics, macros and the vectorizers' costs; no data ABI change; recorded in each object |
| `-msse4.2`, `-mavx2`, `-march=x86-64-v3`, ... | both | in portable mode aliases of the `--isa` level they belong to; in system mode as natively |
| `--vector-width=<bits>` | both | 128 (default) or 256: the width of the fragments vector code runs as; `--isa=x86-64-v3` implies 256 |
| `-fvectorize`, `-fslp-vectorize` and their `-fno-` forms | both | the vectorizers, on by default at `-O2` and above |
| `--native-kernels=<dir>` | portable | a kernel package for functions the program declares |
| `--aot-instruction-set <set>` | NativeAOT | `baseline`, `x86-64-v2`, `x86-64-v3` or `native`; defaults to the highest level of the program's objects |
| `--report-simd` | both | the report above |

## Tests

| Test | What it checks |
| --- | --- |
| `tests/vector-lowering.sh`, `tests/vector-sweep.sh`, `tests/vector-semantics.sh`, `tests/vector-reductions.sh` | vector extensions, the vectorizers' output, 22,377 vector forms of the IR semantics probes and the recognized reductions against native, both modes, with and without hardware intrinsics, as NativeAOT, at 256 bits |
| `tests/x86-intrinsics.sh` | every x86 helper and every intrinsic of Clang's headers against the instructions |
| `tests/simd128.sh`, `tests/wasm-simd128.sh`, `tests/ffmpeg-simd128.sh` | the SIMD128 header and ffmpeg's SIMD128 profile |
| `tests/native-kernels.sh`, `tests/ffmpeg-x86asm.sh`, `tests/cpu-detection.sh` | kernel packages, ffmpeg's nasm profile in both modes and CPU detection |
| `tests/xp/` | the vector, intrinsic, kernel and detection fixtures on Windows x64, macOS ARM64 and Linux ARM64, JIT and NativeAOT |
