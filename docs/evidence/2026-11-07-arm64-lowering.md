# Evidence: ARM64 forms of the vector lowering for x264 (SV-34 to SV-39, 2026-11-07)

Host `mainmac`: Apple M6 (12 cores), macOS 27.2, .NET 10.0.14 (SDK 10.0.403, ILCompiler for `osx-arm64`). Base commit `22a0f55` on branch `x264-perf`. The workload is the one of the [x86 evaluation](2026-11-06-x264.md): SPEC CPU2017 525.x264_r's first refrate invocation (`--pass 1 --bitrate 1000 --frames 1000`), whose Vector128 build ran 1.93 times native NEON on this Mac (11.41 s against 5.90). A development measurement of one SPEC workload, not a SPEC score; the scripts are in the gitignored `artifacts/arm64/` of the worktree.

## Method

The x86 evaluation's portable bitcode objects (`-O3`, the vectorizers' x86-64 cost model) are relinked with each compiler. The ARM64 code is read from RyuJIT's own listing: the program is linked as a JIT image and run for three frames on the Mac with `DOTNET_TieredCompilation=0` and `DOTNET_JitDisasm` naming the hot functions, which gives NativeAOT's instruction selection in under a second per change (counts below are instructions per function, without alignment padding). Timings are NativeAOT: each compiler writes an `--aot-project`, published on the Mac with `dotnet publish -r osx-arm64`, then timed by the x86 evaluation's `run-mac.py`: median of three, round-robin with native clang-22 NEON, every run from a fresh copy of the executable. macOS has no CPU affinity; the Mac was otherwise idle. Every build of every step wrote native's `BuckBunny_New.264` (md5 `225bcd8b…`) and `x264_stats.log` (`15c045d5…`).

x64 must keep its code or get better. Where the ARM64 form is a branch on `AdvSimd.IsSupported` (a constant for the JIT and for ILC), x64 compiles what it compiled before; the check is x264's NativeAOT image for `linux-x64` (`--aot-debug`), whose `__managedcode` section disassembles to the same text before and after.

## SV-34: narrow vector loads and stores as one instruction

An 8-byte vector load (`load <8 x i8>`, a row of 8 pixels) was `Vector128.CreateScalar(ulong)`, which RyuJIT compiles on ARM64 to `movi v, #0`, `ldr x` and `ins v.d[0], x`; a 4-byte one to `movi`, `ldr w`, `ins v.s[0]`; the stores of 8 and 4 bytes moved the lanes to a general register first (`umov` and `str`). Native code is one `ldr d` or `ldr s`, which zeroes the rest of the register, and one `str d` or `str s`. A probe of the candidate forms on the M6 (RyuJIT's listing):

| Form | ARM64 |
| --- | --- |
| `Vector128.CreateScalar(ReadUnaligned<ulong>)` (before) | `movi`, `ldr x`, `ins v.d[0]` |
| `AdvSimd.LoadVector64(p).ToVector128()` | `ldr d`, `mov v.8b, v.8b` |
| `AdvSimd.LoadVector64(p).ToVector128Unsafe()` | `ldr d` |
| `AdvSimd.LoadAndInsertScalar(Zero, 0, p)` | `movi`, `ld1 {v.d}[0]` |
| `Vector128.CreateScalar(ReadUnaligned<float>)` | `movi`, `ldr s`, `ins v.s[0], v.s[0]` |
| `Vector128.CreateScalarUnsafe(ReadUnaligned<float>)` | `ldr s` |
| `ReadUnaligned<ulong>` store of `ToScalar()` (before) | `umov x`, `str x` |
| `AdvSimd.Store(p, value.GetLower())` | `str d` |
| `WriteUnaligned(p, value.AsSingle().ToScalar())` | `str s` |

`Vectors.Load` and `Vectors.Store` now take the ARM64 forms behind `AdvSimd.IsSupported`: 8 bytes `ldr d` and `str d`, 4 bytes `ldr s` and `str s`, 12 bytes `ldr d` with `ld1 {v.s}[2]` and `str d` with `st1 {v.s}[2]`. The `Unsafe` conversions keep the register as the load left it, zero above, which the lowering's padding rule needs (`Vectors.Load` promises zeros above the bytes read, and SV-26 leaves a narrow load's zero padding out); the float view only moves bits, no arithmetic touches them. 2 and 1 bytes keep their form.

| Function (ARM64 instructions) | before | after |
| --- | ---: | ---: |
| `x264_pixel_satd_8x4` | 201 | 169 |
| `x264_pixel_sad_8x8` | 115 | 81 |
| `x264_pixel_sad_x4_8x8` | 451 | 313 |
| `quant_4x4` | 188 | 164 |
| `x264_pixel_ssd_8x8` | 209 | 177 |
| `hpel_filter` | 808 | 769 |

x264 takes 10.38 s instead of 11.18 (native 5.76 in the same rounds, 1.80 times instead of 1.94). x64's code is unchanged.

**Correctness.** The new fixture [tests/c/vector-pixels.c](../../tests/c/vector-pixels.c) has rows of 4, 8 and 16 bytes loaded, computed and stored, row sums of absolute differences, widened differences and sums, a quantization, a Hadamard transform of 16-bit halves and constant shuffles of one and two sources; [tests/vector-sweep.sh](../../tests/vector-sweep.sh) compares it with native clang in both modes at `-O3` and `-march=x86-64-v3`, as NativeAOT and without hardware intrinsics, and the xp bundle runs it on macOS ARM64 and Linux ARM64. On the M6 it, `vector-reductions.c` and `vector-extensions.c` print native's output under the JIT with and without hardware intrinsics.

## SV-35: a row's sum of absolute differences as uabd and uaddlv

x264's SADs reduce each row on its own: the x86 cost model's SLP vectorizer makes `sad_8x8` eight `llvm.vector.reduce.add` of `zext(abs(sub(zext <8 x i8>, zext <8 x i8>)))`, added as scalars, and `sad_16x16` a loop of 16-byte rows. SV-21 makes each one `llvm.x86.sse2.psad.bw` of the row padded to 16 bytes, and SV-26 extracts the 64-bit lane (or two) that summed the row's bytes: `psadbw` and `movq` on x64, as natively. On ARM64 the x86 helper's exact composition of psadbw's two sums is `uabd.16b` and three `uaddlp` (to `.8h`, `.4s`, `.2d`) before the `umov`, where native AArch64 code takes the total with `uabd` and `uaddlv` (or `udot` with ones):

| Form (M6, RyuJIT) | ARM64 |
| --- | --- |
| `X86.Psadbw` and lane 0 (before) | `uabd.16b`, `uaddlp.8h`, `uaddlp.4s`, `uaddlp.2d`, `umov x` |
| `AddAcrossWidening(AbsoluteDifference(l.GetLower(), r.GetLower()))` | `uabd.8b`, `uaddlv h`, `umov w` |
| the same on 16 bytes | `uabd.16b`, `uaddlv h`, `umov w` |
| `Dp.DotProduct(Zero, AbsoluteDifference(l, r), One)` and `AddAcross` | `movi`, `uabd`, `movi`, `udot`, `addv`, `umov` |

A reduction whose tree is one leaf needs only the total, which psadbw's exact two sums do not express, so the recognizer now writes llvmnet's own operation for it: `i64 @llvm.llvmnet.sad(<16 x i8> %left, <16 x i8> %right, i32 %bytes)`, the row padded with zeros as before and its length (2 to 16 bytes), truncated or extended to the reduction's type. Its helper, `Vectors.SumAbsoluteDifferences` (`[LlvmIntrinsic("llvm.llvmnet.sad")]`, which `TargetIntrinsics` now collects from `Vectors` as well as `X86`), runs `uabd` and `uaddlv` on ARM64, the 8-byte forms for rows of at most 8 bytes, and reads the result register's 64 bits (uaddlv zeroes it above the sum), so a row is `ldr d`, `ldr d`, `uabd.8b`, `uaddlv`, `umov`. On x64 it runs `X86.Psadbw` and the 64-bit sums that summed the row, what the recognizer's IR computed before. The result is 64 bits wide because an `int` made RyuJIT spill every row's sum on x64 (`sad_x4_8x16` grew from 540 to 579 instructions). Rows of more than 16 bytes, accumulator loops (a tree of adds and phis) and the products keep psadbw and pmaddwd.

| Function (ARM64 instructions) | base | SV-34 | SV-35 |
| --- | ---: | ---: | ---: |
| `x264_pixel_sad_8x8` | 115 | 81 | 65 |
| `x264_pixel_sad_x4_8x8` | 451 | 313 | 249 |
| `x264_pixel_sad_16x16` | 26 | 26 | 22 |

x264 takes 10.32 s against SV-34's 10.46 in the same rounds (native 5.88), 1.3% and within the noise: the rows were already short after SV-34. x64's code is the same in all but two functions, whose 16-byte rows now add their two sums at once and spill less: `sad_16x8` is 95 instructions instead of 107, `sad_x4_8x16` 527 instead of 540.

**Correctness.** [tests/vector-reductions.sh](../../tests/vector-reductions.sh) checks that `--report-simd` names the helper for the fixture's two row SADs (`pix_abs16`, `pix_abs8`) while the accumulator loops keep `X86.Psadbw`, with native output in every configuration it runs; `vector-pixels.c` has rows of 4, 8 and 16 bytes. Both print native's output on the M6 and on Linux ARM64 under the JIT, with and without hardware intrinsics.

## SV-36: narrowing to half a fragment as one xtn

A vector of half a fragment or less, as the x86 cost model's 64-bit vectors are (`trunc <4 x i32> to <4 x i16>` in x264's `quant_4x4`, the 8-byte pixel rows of `get_ref` and `mc_chroma`), narrowed as `Vector128.Narrow(x, Zero)`, the zero standing for the fragments past the source. On ARM64 that is `xtn`, `movi` and `xtn2`, where `xtn` alone writes the narrowed lanes and zeroes the upper half; `quant_4x4` had eight such sequences. On x64 a narrowing of two steps (32 to 8 bits) computed `Narrow(Zero, Zero)` as well, which RyuJIT does not fold: two `xorps`, `packusdw` and `pand`, hoisted and spilled, then reloaded at each use.

`VectorEmitter.Resize` now narrows the lower half alone when everything above it is zero, through `Vectors.NarrowLower` (and `NarrowWithSaturationLower` for `fptosi.sat`'s saturating steps): `AdvSimd.ExtractNarrowingLower(x).ToVector128Unsafe()` on ARM64 (`xtn`; `sqxtn` and `uqxtn` saturating, `fcvtn` for doubles) and `Vector128.Narrow(x, Zero)` elsewhere, which is what one step compiled to before.

| Function (ARM64 instructions) | SV-35 | SV-36 |
| --- | ---: | ---: |
| `quant_4x4` | 164 | 148 |
| `get_ref` | 776 | 747 |
| `mc_chroma` | 293 | 285 |
| `hpel_filter` | 769 | 761 |

x264 takes 10.16 s against SV-35's 10.22 in the same rounds (native 5.81), within the noise. On x64, 12 functions change: the spilled zero of two-step narrowings is gone and the narrowing's zero is one `xorps` at its use; the other differences are register allocation (`get_ref` 1,181 instructions instead of 1,193, `mc_luma` 1,250 instead of 1,278, `hpel_filter` 1,030 instead of 1,014, where one loop holds one more vector on the stack).

**Correctness.** The vector forms of the IR semantics probes (22,377 lines, every trunc, fptrunc and `fptosi.sat` shape) print native's output on the M6 and on Linux ARM64 under the JIT with and without hardware intrinsics, as do `vector-pixels.c` (its quantization), `vector-extensions.c` and `vector-loops.c`; `vector-lowering`, `vector-semantics` and `vector-sweep` pass on x64.

## SV-37: widening subtracts and adds as usubl and uaddl

`sub(zext a, zext b)` of bytes, every pixel difference in x264 (`satd`, `sa8d` and `hadamard_ac`'s rows as `<4 x i8>` to `<4 x i32>`, the SSDs' `<8 x i8>` and `<16 x i8>` to 32 bits, `sub8x8_dct8`'s to 16 bits), was an `uxtl` per operand and step and a `sub`: `<4 x i8>` to `<4 x i32>` is four `uxtl` and a `sub`. AdvSimd computes the difference of a half of two vectors at twice the width in one instruction, `usubl` or `usubl2`, and `uaddl`, `saddl` and `ssubl` likewise; native `satd_8x4` uses `usubl.8h`.

`VectorEmitter.Binary` now recognizes an add or subtract of two extensions of one narrow type, both `zext` or both `sext`, to twice the lane width (8 to 32 bits) or four times (bytes), and calls `Vectors.AddWidening16` (`32`, `64`) or `SubtractWidening16` (`32`, `64`) per result fragment with its place in the source fragment. On ARM64 that is one `uaddl`/`usubl`/`saddl`/`ssubl` (or the upper form) for twice the width, and one more `uxtl` or `sxtl` for four times: a difference of bytes fits 16 bits as a signed value and widens with its sign, a sum as unsigned. Elsewhere the helper adds the extensions the lowering computed as before, which it also passes: with the extensions left out and computed in the helper, both loads of a row came before it, and RyuJIT no longer folded each 4-byte load into its `pmovzxbd` (`satd_8x4` grew from 176 to 192 x64 instructions under the JIT). The extensions stay where they are on every host and are dead code on ARM64.

The absolute difference of widened bytes (`uabdl`) is not matched: in x264 it occurs only as the leaves of SAD reductions, which psadbw and SV-35's operation take.

The helper reads the narrow operands where the add is, after their last use in the IR, so `FunctionEmitter.ShareFunctionLocals` now counts them as used there (`VectorEmitter.Reads`). Without that, in functions of more than 512 values, whose locals are shared by liveness, a row's second load could take the first's local: x264's output on the M6 differed (three frames under the JIT), as does the new `tall_differences` of `vector-pixels.c` on ARM64.

| Function (ARM64 instructions) | SV-36 | SV-37 |
| --- | ---: | ---: |
| `x264_pixel_satd_8x4` | 169 | 145 |
| `x264_pixel_ssd_8x8` | 177 | 115 |
| `hpel_filter` | 761 | 685 |

x264 takes 9.79 s against SV-36's 10.17 in the same rounds (native 5.80), 3.7% faster. x64's code is the same in every function but `x264_me_search_ref`, where the longer live ranges change which registers 42 instructions use.

**Correctness.** `vector-pixels.c` has differences and squares of rows of 4, 8 and 16 bytes (twice and four times the width), sums of bytes and halfwords and `tall_differences`, one function of 600 values in the order that exposed the shared local; it and the semantics probes print native's output on the M6 and on Linux ARM64 under the JIT with and without hardware intrinsics, and `vector-sweep.sh` checks that `--report-simd` names the widening helpers.

## SV-39: abs2's sign spread as one arithmetic shift

x264's `abs2` takes the absolute value of the two 16-bit halves of a 32-bit lane: `((a >> 15) & 0x10001) * 0xffff` spreads each half's sign over that half, then `(a + s) ^ s`. The vectorizers keep the multiplication, so `satd_8x4`, `sa8d_8x8` and `hadamard_ac` ran `ushr`, `and` (with a `movi` constant) and `mul` per vector on ARM64 (on x64 `psrld`, `pand` and `pmulld`; native SSE2 code shifts, masks, shifts left and subtracts). Native AArch64 code is one `cmlt` per vector: LLVM's AArch64 backend folds the shape (`performMulVectorCmpZeroCombine`).

The rule is generic: in lanes of 2h bits, `mul(and(lshr(x, h - 1), 1 | 1 << h), (1 << h) - 1)` is all ones in each h-bit half where that half of x is negative, which is an arithmetic shift of the halves by h - 1. `VectorEmitter.Binary` now emits that for a multiplication of this shape in 16-, 32- and 64-bit lanes (a comparison with zero for byte halves, which have no arithmetic shift on x64): `sshr` on ARM64, `psraw` or `psrad` on x64. The `and` and the `lshr` emit nothing when the multiplication is their one user (`VectorEmitter.Fused`), and x counts as used at the multiplication for local sharing.

| Function | SV-37 | SV-39 |
| --- | ---: | ---: |
| `x264_pixel_satd_8x4`, ARM64 | 145 | 135 |
| `x264_pixel_satd_8x4`, x64 | 196 | 184 |
| `sa8d_8x8`, x64 | 210 | 178 |
| `pixel_hadamard_ac`, x64 | 737 | 643 |

x264 takes 9.60 s against SV-37's 9.81 on the M6 in the same rounds (native 5.81), 2.1% faster. On x64 the four functions with the shape (`satd_8x4`, `satd_4x4`, `sa8d_8x8`, `pixel_hadamard_ac`) are shorter, each `psrld`, `pand` and `pmulld` one `psraw`, and `pixel_hadamard_ac` has 58 fewer register moves; two other functions differ only in alignment padding.

**Correctness.** [tests/ir/vector-sign-halves.ll](../../tests/ir/vector-sign-halves.ll) computes the shape in 16-, 32- and 64-bit lanes, with the and's operands either way round, and three look-alikes (another factor, another shift, the factor first), and compares each with the halves' arithmetic shift computed directly; `ir.sh` runs it natively and in both modes, `vector-lowering.sh` without hardware intrinsics and at 256 bits and checks that only the look-alikes multiply. It passes on the M6 and on Linux ARM64 with and without hardware intrinsics, at 128 and 256 bits, and `vector-pixels.c`'s `hadamard` prints native's output there.
