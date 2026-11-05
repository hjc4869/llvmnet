# Evidence: SV-21, the widening reductions as psadbw and pmaddwd (2026-11-05)

Host: AMD Ryzen AI MAX+ 395 (Zen 5, AVX-512), Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `b608ff2`. macOS: Apple M6 (`mainmac`); Linux ARM64: the arm64 .NET container under qemu. Design: [12-simd.md](../plan/12-simd.md#level-1-general-vector-lowering) (the two idioms) and the [SIMD survey](2026-11-03-simd-survey.md#hand-written-vectors-against-scalar-and-native-code), which measured them at 1.36 to 1.58 times native with the neutral `Vector128` API and 0.21 to 0.45 with the instructions.

## What changed

- **The recognizer** (`llvmnet_widening_reductions`, [native/vectors.cpp](../../native/vectors.cpp)) runs on every function that stays vector code after `VectorEmitter.Legalize`, as LLVM's x86 backend does in `X86PartialReduction`, because the dataflow crosses blocks (a loop's accumulator phi). From each `llvm.vector.reduce.add` it walks the adds and phis of the reduced type back to their leaves; when nothing but the reduction reads a value of that tree, only the sum of its lanes is observable, so a leaf may be replaced by any vector with the same lane sum. A leaf `abs(sub(zext a, zext b))` of `<N x i8>` (or a select of the same meaning, the difference taken at another width and extended or truncated to the sum's) in 16- to 64-bit lanes becomes `llvm.x86.sse2.psad.bw` on 16-byte pieces; a leaf `mul(ext a, ext b)` in 32-bit lanes whose operands fit 16 signed bits (a `sext` from 16 bits or less, a `zext` from less than 16, a constant) becomes `llvm.x86.sse2.pmadd.wd` on 8 halfwords. With `--vector-width=256`, 32-byte pieces take AVX2's forms. Products of `uint16_t` values, which do not fit, and accumulators whose lanes the program stores keep the literal code. The tree's adds lose `nsw`/`nuw`, since the per-lane partial sums change.
- **The instructions are the Level 2 helpers** (`X86.Psadbw`, `Pmaddwd` and their 256-bit forms): `Sse2.SumAbsoluteDifferences` and `MultiplyAddAdjacent` behind `IsSupported`, the AdvSimd pairwise additions on ARM64 and exact compositions elsewhere. Both instructions are exact for a sum: `psadbw`'s eight differences fit 16 bits, and `pmaddwd`'s one wrapping case (two products of -32768) wraps as the 32-bit sum does.
- **Pairs.** LLVM's x86 cost model vectorizes `int16_t` dot products and byte SADs as 64-bit vectors (`<4 x i16>`, `<8 x i8>`, interleaved by two). Zero-padded to one instruction each, half of every `pmaddwd` multiplied zeros, which costs nothing on x64 but made the AdvSimd composition 20% slower than the literal code on the M6 (dot16 0.326 s against 0.273). Two leaves of one block whose operands fill half a fragment now share one instruction on their concatenated operands (an `unpcklpd` each), and the other leaf becomes zero, so no lanes are padding.
- **Zero-padding shuffles.** A `shufflevector` lane taken from a zero vector is now cleared with one `and` of the other lanes instead of a shuffle and a `ConditionalSelect` (`VectorEmitter.Gather`); the 4-byte SAD leaves need it.
- The hidden `--vector-idioms=off` keeps the literal dataflow, for comparisons; `-v` names each function that got the instructions.

## Correctness

[tests/c/vector-reductions.c](../../tests/c/vector-reductions.c) sums absolute differences into `uint16_t`, `uint32_t` and `uint64_t` accumulators, through `abs` and a select, in ffmpeg's `pix_abs16_c` shape (16 terms per row, which SLP reduces as one vector) and an 8-wide loop, and products of `int16_t`, `uint8_t` by `int16_t`, `int8_t`, a constant and squares, with runs of -32768 that make `pmaddwd` wrap, over lengths 0 to 1031 that exercise every epilogue; and it has the look-alikes: `uint16_t` products, and accumulators of Clang vector extensions whose lanes are stored besides being reduced. [tests/vector-reductions.sh](../../tests/vector-reductions.sh) builds it in both modes at `-O2` and `-O3`, with `DOTNET_EnableHWIntrinsic=0`, at `--vector-width=256` with and without `DOTNET_EnableAVX=0`, at `--isa=x86-64-v3` (also with AVX2 and hardware intrinsics off), with `--vector-idioms=off` and as NativeAOT at `-O2` and `x86-64-v3`: every build prints what native clang prints, and `-v` names the 13 recognized functions and none of the three look-alikes. The JIT images match native on Linux ARM64 with and without hardware intrinsics. The fixture is in the xp bundle at `-O2` and at `x86-64-v3`.

## The survey's kernels

`artifacts/simd-x86/reductions-kernels.sh 5` (the survey's `kernels.c`, `-O2`, which vectorizes since SV-05), median seconds; checksums equal in every build:

| kernel | native scalar | native SSE2 | JIT literal | JIT idioms | JIT literal, tiering off | JIT idioms, tiering off | NativeAOT literal | NativeAOT idioms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| sad8 | 0.632 | 0.124 | 0.284 | 0.275 | 0.298 | 0.208 | 0.338 | 0.164 |
| dot16 | 0.653 | 0.148 | 0.219 | 0.222 | 0.239 | 0.234 | 0.424 | 0.171 |

On the M6 (the same JIT images): sad8 0.303 literal and 0.301 with the idioms, dot16 0.274 and 0.271; with tiering off 0.309 and 0.306, 0.267 and 0.273.

- **NativeAOT** at its default instruction set gains 2.1 times on sad8 and 2.5 on dot16, to 1.3 and 1.2 times native SSE2: without SSE4.1 it composes the literal code's `pmovzx`/`pmovsx` widenings and `pmulld`, and `psadbw` and `pmaddwd` are SSE2.
- **The JIT** on this Zen 5 has SSE4.1 and AVX2, where `vpmulld` is cheap, so dot16 is level; sad8 gains 1.43 times in optimized code (tiering off). With default tiering each kernel is one call whose loop runs as on-stack-replacement code, and that code's speed depends on where it lands: the idioms' sad8 OSR code is byte-identical whether it runs 0.257 or 0.205 s (`DOTNET_OSR_HitLimit=1`, or `DOTNET_JitAlignLoops=0`, moves it). Long single-call loops are a benchmark shape; a hot function called repeatedly is promoted to Tier1.
- **ARM64** is level, where the literal code is native widening arithmetic too.

## ffmpeg's SAD

`artifacts/simd-x86/ffmpeg-sad.sh`: the FATE build's ffmpeg 9.0.2 (`artifacts/ffmpeg-fate-{portable,native}`, built with `-fno-vectorize`) with `libavcodec/me_cmp.c` recompiled at ffmpeg's `-O3` with the vectorizers and the archive's member replaced, relinked; an mpeg4 encode of 300 720p frames on one thread with `-dia_size 4`, so that motion estimation dominates (native: 0.574 s scalar, 0.247 vectorized). The recognizer finds `pix_abs16_c`, `pix_abs8_c`, `vsad_intra16_c` and `vsad_intra8_c` (the half-pel `_x2`, `_y2` and `_xy2` forms subtract an average, not a byte, and keep the literal code). Every build writes the same packets. Median of 7, seconds:

| build | scalar `me_cmp.c` | vectorized, literal | vectorized, idioms |
| --- | ---: | ---: | ---: |
| JIT | 1.548 | 1.316 | 1.321 |
| JIT, `DOTNET_TC_CallCountingDelayMs=0` | 1.434 | 1.169 | 1.104 |
| NativeAOT | 0.635 | 0.511 | 0.400 |

The JIT's startup is 0.477 s (`ffmpeg -version`). As NativeAOT the idioms make the encode 22% faster than the literal vector code and 1.6 times faster than the scalar build, at 1.6 times native vectorized; under the JIT, net of startup, 9% faster with the tiering setting M12 measured (JP-02) and level with the default delay, which keeps much of the encode at Tier0.

## dav1d's C paths

`artifacts/simd-x86/dav1d-sv21.sh`: dav1d 1.5.4 with assembly off, built by Meson with this llvmnet at `-O2` and natively with clang-22 at `-O2` (vectorized) and `-O2 -fno-vectorize -fno-slp-vectorize`; the CLI's link run again with `-v`. The recognizer finds dav1d's 8-tap filters, `put_8tap_c`, `prep_8tap_c` and their scaled forms (nine reductions in five functions): each output pixel sums eight products of a pixel and a signed coefficient, which the SLP vectorizer reduces as one vector. Decoding the library survey's 1080p clip (188 frames, `artifacts/oss/scripts/dav1d-bench.sh`) on one thread, every build's md5 is native's; median of 5, seconds:

| native scalar | native vectorized | JIT literal | JIT idioms | NativeAOT literal | NativeAOT idioms |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 4.722 | 3.214 | 4.834 | 4.722 | 7.286 | 7.112 |

The filters gain 2 to 2.5% of the whole decode. The vectorized build is 1.5 times slower as NativeAOT than under the JIT, which SV-05's library measurements take up.

## The benchmark set

No program of the benchmark set has a reduction the recognizer takes (its 53 non-ffmpeg programs compiled with the vectorized engines' flags and `-v`). Each program's linked IR translated by master's compiler and by this one gives byte-identical images, so the zero-padding change did not touch them either.

## `--report-simd` (SV-10)

`--report-simd` reads the written image's IL: per function the calls of `Vector128` and `Vector256` operations and of the runtime's SIMD helpers, each helper classed by the instruction sets whose `IsSupported` its own IL tests (and the 128-bit helpers a 256-bit one calls), with the scalarized functions and the reductions above. dav1d's CLI, linked again with it, begins:

```text
llvmnet: simd: 117 functions make 10960 Vector128 operations and 9 SIMD helper calls; 7 functions with vector IR run as scalar code
llvmnet: simd: scalarized dav1d_cdef_brow_8bpc (its vector code moves more lanes than it computes on)
...
llvmnet: simd: reductions of put_8tap_c: 3 sums of products as pmaddwd
llvmnet: simd: reductions of put_8tap_scaled_c: 1 sum of products as pmaddwd
llvmnet: simd:   calls x64                    ARM64                  helper: functions
llvmnet: simd:       9 Sse2                   AdvSimd.Arm64          X86.Pmaddwd (llvm.x86.sse2.pmadd.wd): prep_8tap_c, prep_8tap_c.1034, prep_8tap_scaled_c, put_8tap_c (+1)
llvmnet: simd: operations  calls function
llvmnet: simd:       1278      3 put_8tap_c
llvmnet: simd:        717      3 prep_8tap_c
llvmnet: simd:        714      0 put_8tap_c.1036
```

The 16-bit-pixel `put_8tap_c.1036` keeps its literal code: `uint16_t` pixels do not fit `pmaddwd`'s signed halfwords. For the SSE conformance program the table shows what runs as an instruction on which host: `X86.Pshufb` as `Ssse3` and `AdvSimd.Arm64`, `X86.Crc32b` as `Sse42` and `Crc32` (through `BitOperations.Crc32C`, whose ARM64 branch the x64 CoreLib's IL does not contain, so the report names it from a table), and the SSE4.2 string comparisons as composed on both, since .NET has no intrinsic for them. `tests/vector-reductions.sh` checks the report's reductions and helper classes at the baseline and at `x86-64-v3`.

## What it does not show

- An Intel host, where `vpmulld` costs two micro-operations and ten cycles, so the JIT's dot products should gain there as NativeAOT's do here.
- `pavgb`-based half-pel SADs, `pmaddubsw` (unsigned by signed bytes, which saturates) and VNNI's dot products, which LLVM's x86 backend also matches.
