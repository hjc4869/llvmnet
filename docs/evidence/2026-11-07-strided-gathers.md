# Evidence: strided gathers as wide loads and a shuffle (SV-40, 2026-11-07)

Host: AMD Ryzen AI MAX+ 395 (Zen 5), Linux 6.12.115, .NET 10.0.14 (ILCompiler 10.0.14), Debian clang 22.1.8, shared with five other sessions' builds and benchmarks; and `mainmac`, an Apple M6 with macOS 27.2 and .NET SDK 10.0.403. Base commit `22a0f55` (branch `x264-perf`). The x264 workload, builds and scripts are those of [2026-11-06-x264.md](2026-11-06-x264.md) (`artifacts/x264-perf/`, the Mac's in `artifacts/x264-perf/mac/`); this item's copies and results are in the gitignored `artifacts/strided-gathers/`.

## The gap

With the x86-64 SSE2 cost model, which portable bitcode is optimized with on every host, the loop vectorizer scalarizes interleaved groups whose wide loads and shuffles it prices too high. A gather of every second byte then becomes one scalar load and one `insertelement` per lane. x264's `frame_init_lowres_core` (the lowres filter, `FILTER(src0[2x], src1[2x], src0[2x+1], src1[2x+1])` and three more per output pixel) gathers 144 bytes per eight output pixels that way: 240 `pinsrb` at Vector128 on x86 (2.6 times native SSE2, which packs pairs into words with 32 `pinsrw`), and 256 `mov.b v[i], w` with 144 `ldrb` on ARM64. On the M6 the function ran 51 times native, which uses `ld2` and `urhadd`, and took 28% of the Vector128 gap (item 1 of `artifacts/x264-perf/mac/REPORT.md`). The same cost model leaves simpler gather loops (`d[i] = s[2 * i] + 1`, strides 3 and 4, 16-bit lanes) as inserts, which the typed lowering's cost then scalarized, since they move more lanes than they compute on.

## What changed

`llvmnet_strided_gathers` ([native/vectors.cpp](../../native/vectors.cpp)) runs from `VectorEmitter.Legalize` on each function the typed lowering translates.

- **What it finds.** A gather is an `insertelement` chain from poison that sets every lane (at least four, of 1 to 8 bytes) once from a simple load of one block. The lanes' addresses must decompose into the same root, the same variable terms and constant offsets one stride apart: a multiple of the lane size, within 64 bytes in all. The root is where a chain of inbounds `getelementptr`s starts. The terms are 64-bit adds, disjoint ors, subtractions, and shifts or multiplications by constants, taken modulo 2^64; the vectorizer's `or disjoint (shl %i, 1), 2` are such terms. No instruction between the lanes' loads may write memory or be a barrier.
- **What it may read.** The lanes become 16-byte pieces (fewer bytes for a shorter gather) that one `shufflevector` takes the lanes out of. A piece reads bytes between and around the lanes only where the block shows them dereferenceable. Within a stretch of the block that has no call (which may free the object) and no instruction after which the block may not go on, every simple load or store through the same root and terms is in the root's object. The object is contiguous, so every byte from the lowest to the highest of those accesses is readable. So are the bytes the root is known to point to (an alloca, a global, a `dereferenceable` argument or load: `getPointerDereferenceableBytes`) when the address has no variable terms. Where no window of whole pieces fits in those bytes, the pieces start at the first lane and end at the last one, overlapping, and a gather shorter than its piece reads two halves.
- **Sharing.** Gathers that see one memory (no write between them) and use the same range share pieces at the same address. Interval stabbing picks each window's offset among those that fit, so that as few distinct pieces load as possible. Pieces load before the last lane load of the first gather that uses them, at the alignment lane 0's load guarantees at their offset.
- **Cost and lowering.** A dry run counts the inserts it would remove, and `Legalize`'s cost leaves them out, so gather loops stay vector code. With 128-bit fragments, a shuffle whose result fragment takes every other lane of one or two source fragments (these gathers, and the loop vectorizer's own interleaved groups) is `Vectors.UnzipEven` or `UnzipOdd`, or `EvenLanes` or `OddLanes` of one fragment. These are `uzp1`/`uzp2` on ARM64; on x64, `packuswb` of masked or shifted lanes, SSE4.1's `packusdw` (else `packssdw` of sign-extended halves), `shufps`, `unpcklpd`/`unpckhpd`, and `pshufb` or `pshufd` for one fragment where the host has SSSE3; `Narrow` elsewhere. Other constant shuffles of 8- and 16-bit lanes go through `Vectors.ShuffleBytes`, `pshufb` where the host has SSSE3. ILC tests that at run time at the baseline instruction set, where `Vector128.Shuffle` of such lanes runs in software. When two to four source fragments each need a shuffle, they are one call: byte shuffles whose 0x80 indices zero the lanes the other sources supply, or'ed, instead of a shuffle and a blend per source.
- **Reporting and scope.** `-v` and `--report-simd` print "N strided gathers from M wide loads" per function. The hidden `--vector-idioms=off` leaves the gathers as inserts. With 256-bit fragments, whose IR comes from the AVX2 cost model (which loads interleaved groups whole: x264's function has `load <64 x i8>` and shuffles at `x86-64-v3`), gathers are left alone.

In x264, `frame_init_lowres_core` has 16 gathers from 8 wide loads: one 16-byte load per source row in each stretch between the output stores, out of which `EvenLanes` and `OddLanes` take the lanes. The window that starts one byte later serves both the odd lanes and the even lanes that follow them. `x264_cabac_context_init` has 8 gathers, and `x264_rdo_init` 1, which now stays vector code. Those three functions are the only ones whose code shrank much (by 2,899, 880 and 1,175 bytes). `x264_me_search_ref` (−45 bytes, 4 `pblendvb` become `por`) and `pixel_hadamard_ac` (+27) are the only other functions of the 641 whose code changed.

## Machine code of frame_init_lowres_core

| | instructions | inserts | scalar byte loads | de-interleaves |
| --- | ---: | ---: | ---: | ---: |
| x64 before (NativeAOT `x86-64-v2`) | 1,098 | 240 `pinsrb` | (folded into `pinsrb`) | 2 `pshufb` |
| x64 after | 610 | 0 | 0 | 18 `pshufb` |
| ARM64 before (NativeAOT `osx-arm64`) | 1,104 | 256 `mov.b` | 144 `ldrb` | 0 |
| ARM64 after | 512 | 0 | 16 `ldrb` (the scalar epilogue) | 16 `uzp1`/`uzp2` |

## Measurements

**x264, x86 (Zen 5), Vector128 against SSE2.** These are NativeAOT `x86-64-v2` builds of SPEC's flags, linked from the same objects by the baseline toolchain and by this one. Timings are 3 round-robin rounds, each run from a fresh copy, pinned to CPU 13 and its SMT sibling 29. The host was never quiet: five other sessions built, tested and timed throughout, and their load put every run above the 14.83 s of 2026-11-06.

| Run | before (s) | after (s) | native SSE2 (s) | change |
| --- | --- | --- | --- | ---: |
| 23:20, load average 18 (a toolchain of this change before `ShuffleBytes`; `frame_init_lowres_core`'s code is the same) | 17.72, 18.14, 17.59 | 16.57, 17.34, 16.70 | | −5.7% |
| 00:08, load average 36 to 41, every core 80 to 100% busy (the final toolchain) | 26.42, 24.90, 25.49 | 27.59, 24.67, 24.97 | 23.26, 21.48, 23.54 | −2.0% |

The second run measures the host more than the code: native itself took 1.9 times its uncontended 12.40 s. Every run wrote the same `BuckBunny_New.264` (md5 `225bcd8b…`) and `x264_stats.log` (`15c045d5…`) as native. The `LD_PRELOAD` sampler, run on `--aot-debug` builds of both toolchains at once, puts `frame_init_lowres_core` at 0.97 s before and 0.11 s after; native SSE2 took 0.34 s in the 2026-11-06 profile. `x264_me_search_ref` went from 0.68 to 0.61 s, and no other function moved by more than the contention between the two runs. In the 2026-11-06 profile the function took 0.92 s of the 14.83 s. At a ninth of that, x264 should take about 14.0 s on a quiet host, 1.13 times native's 12.40 s; this is an estimate, not a measurement.

**x264, ARM64 (M6), Vector128 against NEON.** The same portable objects were linked as NativeAOT projects by the two toolchains and published on the Mac (`osx-arm64`). Timings are 3 round-robin rounds with fresh copies, beside the native clang-22 NEON build of the 2026-11-06 report.

| Build | runs (s) | median (s) | / native |
| --- | --- | ---: | ---: |
| llvmnet before | 11.39, 11.24, 11.24 | 11.24 | 1.93 |
| llvmnet after | 10.00, 9.77, 9.80 | 9.80 | **1.69** |
| native NEON | 6.03, 5.80, 5.81 | 5.81 | |

That is −12.8%, with every output matching; an earlier toolchain of this change, without `ShuffleBytes`, measured the same (11.26 to 9.76 s). The Mac sampler puts `frame_init_lowres_core` at 1.56 s before and 0.07 s after (native: 0.03 s), 99% of the gain.

**Gather loops, x86.** The micro-benchmark `artifacts/strided-gathers/probe` runs each loop over 1,024 outputs, 100,000 times, as NativeAOT pinned to CPU 13. The table gives ns per call, the lowest of five alternating runs. Native is `clang-22 -O3` (SSE2), and "baseline" is `--aot-instruction-set baseline`. All builds print the same checksum.

| Loop | before | after | before, baseline | after, baseline | native |
| --- | ---: | ---: | ---: | ---: | ---: |
| lowres filter (two outputs) | 2,808 | 290 | 2,759 | 413 | 1,292 |
| `s[2i] + 1` | 272 | 46 | 262 | 62 | 265 |
| `s[3i] * 3` | 277 | 90 | 274 | 137 | 259 |
| `s[4i + 1] ^ 5` | 269 | 69 | 267 | 90 | 241 |
| 16-bit `s[2i + 1] + 1` (the vectorizer's own interleaved loads) | 108 | 106 | 2,481 | 163 | 67 |
| 16-bit `s[3i] * 3` | 231 | 92 | 243 | 174 | 254 |
| 16-bit `s[4i + 2] ^ 5` | 194 | 97 | 175 | 195 | 254 |
| `(s[2i] + s[2i + 1] + 1) >> 1` | 324 | 65 | 625 | 52 | 545 |
| RGB to gray, stride 3 | 538 | 206 | 857 | 365 | 549 |

Before, the cost model scalarized `s[2i] + 1`, the stride-3 and stride-4 loops; they now stay vector code. At the baseline instruction set, the first version of this change ran them 8 to 15 times slower than the scalar code. ILC compiles `Vector128.Shuffle` of 8- and 16-bit lanes there as a call to the software fallback, and a shuffle from several pieces took one of those per piece and a blend each, every one behind its own run-time ISA test with the vectors spilled around it. Hence `Vectors.ShuffleBytes`: one `Ssse3.IsSupported` test for all the pieces of a result fragment, and `por` instead of blends. The 16-bit stride-4 loop is still about level with its scalar code at baseline. The 16-bit `s[2i + 1]` loop was 2.5 µs at baseline before, because its interleaved loads' `Vector128.Shuffle` ran in software, and is 163 ns with `OddLanes`.

## Tests

- [tests/c/strided-gathers.c](../../tests/c/strided-gathers.c) has the vectorizer's shapes: the lowres filter, RGB to gray, strides 2 to 4 of bytes and 16-bit values, over 0 to 160 elements. It also builds vectors of strided elements with Clang's vector extensions, at strides 2 to 4 of 1-, 2-, 4- and 8-byte lanes (8 and 16 lanes), and has an even-plus-odd pair and odd lanes followed by the next even ones; and pairs of 16-bit, 32-bit and float lanes that the vectorizer loads whole and splits into even and odd lanes of two fragments (`UnzipEven`/`UnzipOdd`). Every source lies against a page mapped `PROT_NONE`, ending at the page's last byte or starting at the next page's first, so a gather that read past its last lane or before its first would fault natively and in system mode (glibc's `mprotect`; portable mode's host layer does not protect memory). It runs in [tests/vector-sweep.sh](../../tests/vector-sweep.sh)'s profiles (`-O3` with the vectorizers, AVX2) in both modes. [tests/vector-lowering.sh](../../tests/vector-lowering.sh) also runs it at `-O1` and `-O3` in both modes, under the JIT with SSE2 only (`DOTNET_EnableSSE42=0`, the packs) and without hardware intrinsics, and as NativeAOT at `x86-64-v2` and baseline. Every build prints native's checksum.
- [tests/ir/strided-gathers.ll](../../tests/ir/strided-gathers.ll) checks the plans through the `-v` lines, and its `main` checks the values. A global's and a `dereferenceable(16)` argument's lanes 1 to 15 take one load each, past the last lane. An isolated gather takes two halves. A gather covered by a load of the next byte takes one load, but not when a call separates the two (two). Even and odd lanes share one load. A 16-bit stride-3 gather takes three pieces, and a gather of doubles four. Lanes that are not inbounds of one base, and lanes with a store between them, are not gathers. `tests/ir.sh` runs it natively and in both modes.
- The core gate (`tests/run.sh --tag core`, 104 tests) passes, and `tests/vector-lowering.sh` and `tests/vector-sweep.sh` pass on their own. A scratch check of the four unzip helpers against a lane-by-lane definition, for every lane type, passes under the JIT on x64 (SSE4.1, SSE2 only, no hardware intrinsics) and on the M6 (AdvSimd, no hardware intrinsics).

## Left open

- Proofs stay within a block. A loop's next iteration, or the scalar epilogue the vectorizer keeps after a group with a gap (`s[2i]` alone reads `s[2i + 2]` in the next iteration), is not used. Such a gather takes the two-piece form, two loads and two byte shuffles or'ed, where one load and one shuffle would do.
- InstCombine's and VectorCombine's partial widenings (lane 0 loaded wide when the object's size allows, the other lanes inserted or shuffled in a chain) are not gathers by this rule.
- 256-bit fragments: once SV-41 keeps vectors of at most 128 bits as `Vector128` there, the 16-byte pieces and the unzip helpers would apply unchanged. The rewrite is off at 256 bits until then.
