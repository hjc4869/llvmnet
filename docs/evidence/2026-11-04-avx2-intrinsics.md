# Evidence: SV-09, the x86-64-v3 intrinsics (2026-11-04)

Host: AMD Ryzen AI MAX+ 395 (Zen 5), Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `cc038f2`. Linux ARM64 in the arm64 .NET container under qemu. Design: [12-simd.md](../plan/12-simd.md) (Level 2); user documentation: [x86-intrinsics.md](../user/x86-intrinsics.md).

## What Clang emits

A census compiled every function and immediate macro of the x86-64-v3 headers (`avxintrin.h`, `avx2intrin.h`, `fmaintrin.h`, `f16cintrin.h`, `bmiintrin.h`, `bmi2intrin.h`, `lzcntintrin.h`) at `-O0`, `-O1` and `-O2` with `-march=x86-64-v3`. Of their 359 functions, 231 become generic vector IR, which the typed vector lowering already translates (Level 1). The others leave 120 target intrinsics and the macros 22 more, and `-O0` keeps three that `-O1` folds (`avx2.pmovmskb` and the 256-bit `movmsk` forms), 145 in all: AVX's floating-point operations, conversions, rounding, dot product, permutes, tests and masked loads and stores; AVX2's integer operations, shifts by a vector, by an immediate and by each element's own count, packs, `pshufb`, `psadbw`, `mpsadbw`, the cross-lane permutes and the 16 gathers; FMA's `vfmaddsub` (the other FMA intrinsics are `llvm.fma`); F16C's `vcvtps2ph` (`vcvtph2ps` is a generic conversion); and BMI's `bextr`, `bzhi`, `pdep` and `pext` (LZCNT and TZCNT are `ctlz` and `cttz`).

## The helpers

[X86Avx.cs](../../src/LlvmNet.Runtime/Simd/X86Avx.cs) holds one helper per intrinsic (144 methods for the 145 names, as `vzeroupper` and `vzeroall` share one). Each runs the instruction behind `Avx.IsSupported`, `Avx2.IsSupported`, `Fma.IsSupported`, `Bmi1.IsSupported` or `Bmi2.IsSupported`. Without it, a 256-bit helper runs SSE-level helpers on the two 128-bit halves, which is exact because these instructions work within 128-bit lanes. .NET does not accelerate `Vector256` without AVX2, so no fallback computes with whole `Vector256` values. The operations across lanes have their own compositions:

- `vpermd` and `vpermps` select from both halves.
- Masked loads and stores touch only the selected elements, so a masked access at the end of a buffer cannot fault.
- Gathers load where the mask's sign bit is set, with signed indices.
- `vptest` and `vtestps` combine the halves' flags.
- The variable shifts clamp their counts and use AdvSimd's shifts on ARM64.
- `vfmaddsub` negates the addend's even elements, which is exact, and fuses once with `Vector128.FusedMultiplyAdd`.
- .NET has no F16C intrinsics, so `vcvtps2ph` rounds each float to half precision exactly, in every rounding mode, with NaNs quieted and their payloads truncated as the instruction does.

The backend passes a 256-bit vector of two `Vector128` fragments, the default width, to a helper's `Vector256` and back (`VectorEmitter.Convert`). With `--vector-width=256` it passes the vector as it is.

## The level

`--isa=x86-64-v3` gives the frontend `-march=x86-64-v3 -mno-cx16`, keeps the x86 macros, records the level in objects and images, and is the target of `-mavx`, `-mavx2`, `-mfma`, `-mf16c`, `-mbmi`, `-mbmi2`, `-mlzcnt`, `-mmovbe` and `-march=x86-64-v3` in portable mode. The highest level a command line names applies. `-march=x86-64`, which skcms puts before `-mavx2 -mf16c` for its AVX2 file, now passes through without changing the level. AVX-512's flags stay rejected. A NativeAOT program with x86-64-v3 objects gets ILC's `x86-64-v3` instruction set (proposed for confirmation in [decisions.md](../plan/decisions.md#proposed-for-the-owner-2026-11-04-sv-09)). The level also implies `--vector-width=256` (SV-06's 256-bit fragments, which suit the AVX2 hosts the level targets), for a command line that names it or an image of objects compiled at it; an explicit `--vector-width` wins.

Two backend changes came from libjxl:

- **`llvm.fmuladd` rounding.** llvmnet rounded every `llvm.fmuladd` twice. Native code rounds it once where the function's target has FMA. A preparation pass (`llvmnet_fuse_multiply_adds`) now makes it `llvm.fma` in functions whose target features include `+fma`, before the target attributes are removed. Afterwards `a * b + c` at x86-64-v3 gives native's fused result, and the baseline still gives native's two roundings. Before the change, libjxl's AVX2 decode differed from native in its pixels.
- **Wide integers.** InstCombine turned a broadcast of element 1 of a `<8 x float>` into `bitcast` to `i256`, `lshr` by 32 and `trunc` to `i128`, which llvmnet, without integers over 128 bits, rejected. `llvmnet_narrow_vector_integers` rewrites such parts back into shuffles or element extracts, and comparisons of such an integer with zero or all ones into or- and and-reductions.

## Conformance

[tests/c/x86-avx-conformance.c](../../tests/c/x86-avx-conformance.c) calls every helper by its LLVM name. It uses the SSE test's generators: random and special values, every immediate, shift counts within and beyond the width, in-range gather indices of both signs, and masks of every pattern. It must print what the native instructions print; the test checks that it reaches every helper and only them. The level 3 of [tests/c/x86-headers.py](../../tests/c/x86-headers.py) does the same for the 270 functions and macros of the v3 headers.

| run | conformance (80 lines) | headers (270 intrinsics) |
| --- | --- | --- |
| JIT `-O0`, every bit | equal, except `vfmaddsub`'s NaN payloads | equal, except the NaN payloads of 7 FMA functions |
| JIT `-O0` with `DOTNET_EnableAVX2=0`, `EnableAVX=0`, `EnableFMA=0`, `EnableBMI2=0`, `EnableHWIntrinsic=0` (NaNs canonical) | equal | equal |
| JIT `-O2`, `--vector-width=128` and 256 (the level's default), system mode (NaNs canonical) | equal | equal |
| NativeAOT at `x86-64-v3` and at the baseline (NaNs canonical) | equal | equal |
| Linux ARM64, JIT (NaNs canonical) | equal | equal |

The FMA payloads are the one difference in exact bits: when several operands are NaN, the instruction form decides which propagates, and RyuJIT picks the form by register allocation (Clang's choice is fixed). The approximations `vrcpps` and `vrsqrtps` count no result outside the documented error. The xp bundle carries the conformance program (`x86-avx-conformance`), which passes under the JIT and as NativeAOT on Windows x64 (an AVX2 host, where NativeAOT targets `x86-64-v3`) and macOS ARM64, and under the JIT on Linux ARM64; the whole bundle passes on Windows x64 and macOS ARM64 (77 each, 4 skipped for want of GLES and Vulkan libraries).

## libjxl

libjxl 0.12.0 compiled with Highway's AVX2 target statically (`HWY_COMPILE_ONLY_STATIC` at x86-64-v3, with `HWY_DISABLE_PCLMUL_AES` because the runtime has no AES helpers; AVX-512 paths off), by llvmnet and natively with the same switches (`artifacts/simd-x86/libjxl/build.sh`, the survey's dependencies). skcms keeps its baseline transforms in both, because its Haswell transform converts with `_Float16` vectors, a `half` type llvmnet does not have, and in llvmnet's build its stage chaining uses no `musttail` (`SKCMS_HAS_MUSTTAIL=0`), as its 32-byte vector arguments cannot be tail calls on Windows x64 and ARM64.

Outputs, JIT and NativeAOT against the native AVX2 build:

- **Bit for bit:** the decodes of the 4K lossy and of the 944 × 944 HDR images; the lossless (`-e 3 -d 0`), `-e 3 -d 1`, `-e 5`, `-e 7 -d 0.5` and HDR `-e 7` encodes; the JPEG transcode.
- **A few bytes different:** `-e 7 -d 1` (349,952 bytes against 349,954), `-e 4`, `-e 6`, `-e 8` and `-e 7` at `-d 2` and `-d 3`.

The scalar builds differ the same way: llvmnet's EMU128 build against the survey's native EMU128 one differs at `-e 4` and `-e 8`, and so did the survey's build of 2026-11-01, which the survey had not run at those levels. So the cause predates SV-09. libm was ruled out: logged through a preload shim, the native AVX2 `-e 7` encode makes 9.86 million libm calls, mostly `log1pf` and `pow`. musl's results differ from glibc's in the last bit for 3,932 of them, but with musl's own functions preloaded into the native build, every call gives llvmnet's result and the encodes still differ. The remaining difference between the builds is the C++ library: libc++ in portable mode, libstdc++ natively. libjxl's encoder sorts with ties, for example the clusters by count in `enc_heuristics.cc` with `std::sort` and no tie-break, and the two libraries order tied elements differently. This was not confirmed, because no native libc++ is installed.

Timings: median seconds of three runs in alternating order (`scripts/ab-time.py`), each run a new process, so the JIT columns include startup and compilation (about 0.5 s; djxl's NativeAOT runs about 0.1 s of startup). `native scalar` is the survey's EMU128 build at `-O2 -fno-vectorize`. The llvmnet AVX2 build ran once with the default 128-bit fragments and once with 256-bit ones, which `--isa=x86-64-v3` now implies (below); both decode and encode the same bytes.

| workload, threads | native AVX2 | native scalar | llvmnet AVX2 NativeAOT, 256-bit | 128-bit | llvmnet AVX2 JIT, 256-bit | 128-bit | llvmnet scalar NativeAOT | scalar JIT |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| cjxl 4K `-e 7 -d 1`, 1 | 1.097 | 5.143 | 2.287 | 2.875 | 4.496 | 7.787 | 10.011 | 11.398 |
| cjxl 4K `-e 3 -d 1`, 1 | 0.195 | 0.434 | 0.333 | 0.339 | 0.989 | 1.182 | 0.695 | 1.359 |
| djxl 4K lossy, 1 | 0.084 | 0.269 | 0.326 | 0.368 | 0.747 | 0.809 | 0.544 | 0.937 |
| cjxl 4K `-e 7 -d 1`, 4 | 0.446 | 1.530 | 0.840 | 0.980 | 2.044 | 3.414 | 2.901 | 4.199 |
| cjxl 4K `-e 3 -d 1`, 4 | 0.142 | 0.221 | 0.214 | 0.218 | 0.756 | 0.993 | 0.307 | 1.000 |
| djxl 4K lossy, 4 | 0.030 | 0.078 | 0.116 | 0.134 | 0.567 | 0.612 | 0.179 | 0.607 |

As NativeAOT with 256-bit fragments, Highway's AVX2 target makes the `-e 7` encode 4.4 times faster than llvmnet's scalar build (2.29 against 10.01 s) and takes 2.1 times as long as native AVX2, where llvmnet's scalar build takes 1.95 times as long as native scalar; with four threads, 3.5 times faster and 1.9 times native. The 256-bit fragments are worth 1.26 times as NativeAOT and 1.7 times under the JIT on that encode.

The image runs everywhere: with `DOTNET_EnableAVX2=0` it decodes the HDR image bit for bit as native. With `DOTNET_EnableHWIntrinsic=0`, and on Linux ARM64 (which gives the same bytes as hardware intrinsics off), Highway warns that the CPU lacks its static target and decodes values that differ in the last bits. The cause is the decoder's `vrcpps`: without the instruction, the helper divides exactly, which the instruction's documented error allows.

## What remains

Skia's AVX2 path (SkOpts' HSW functions, compiled with `-march=haswell`, and the raster pipeline's 8-lane `skvx`) is not measured: Skia's portable build uses `target_cpu = "generic"`, and its x64 build would also select its AVX-512 functions on this host. `_Float16` arithmetic (LLVM's `half`) is not translated.
