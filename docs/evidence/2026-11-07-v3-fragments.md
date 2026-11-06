# Evidence: vector fragments sized per value (SV-41) (2026-11-07)

Host: AMD Ryzen AI MAX+ 395 (Zen 5), Linux 6.12.113, .NET 10.0.14 (ILCompiler 10.0.14), Debian clang 22.1.8. Base commit `22a0f55` (branch `x264-perf`). The workload, builds and tools are those of [2026-11-06-x264.md](2026-11-06-x264.md): SPEC CPU2017 525.x264_r's first refrate invocation, toolchains packaged with `scripts/package-toolchain.sh` (`artifacts/spec-portable-toolchain-x264-sv41base` at the base, `-sv41` with the change), `artifacts/x264-perf/build.sh`, and the `LD_PRELOAD` sampler against `--aot-debug` builds. Every run below wrote native's bitstream and statistics (md5 `225bcd8b…`, `15c045d5…`).

**Host load.** Five sibling sessions shared the host during these measurements (load average 20 to 78 on 32 logical CPUs), and a round-robin of single runs varied by 10 to 20% between rounds. The A/B comparisons therefore run both builds at the same time, each pinned to its own core (`taskset -c 14` and `-c 15`, swapped every round) from a fresh copy, and compare their user times; the per-function profile pairs ran the same way. Absolute times are 1.6 to 1.8 times those of the unloaded host in 2026-11-06-x264.md.

## SV-41: fragments sized per value

At `--isa=x86-64-v3`, which implies `--vector-width=256`, every vector was held as `Vector256<T>` fragments: a `<4 x i32>` of the SLP vectorizer was the low half of a `Vector256<int>`, and its operations, shuffles and conversions computed the upper half too. Before SV-24 to SV-30, x264 built with 128-bit fragments for the whole module was 3% faster ([2026-11-06-x264.md](2026-11-06-x264.md#the-result-374439a)), though that gave up AVX2 for the loop vectorizer's 256-bit loops.

### What changed

- **Shapes** (`TypeSystem.Shape`): with 256-bit fragments, a vector of at most 128 bits is one `Vector128<T>` and a wider one `Vector256<T>` fragments (a `<24 x i8>` one `Vector256`, a `<64 x i8>` two). Masks keep the lane widths the module's width gives (`<8 x i1>` has 32-bit lanes, `<4 x i1>` 64-bit ones), so the loop vectorizer's masks stay what they were and a mask of 128 bits or fewer (`<2 x i1>`) is a `Vector128`. `VectorShape.Bits` is the value's own width; `--vector-width=128` modules are unchanged (x264's Vector128 build has a byte-identical `__managedcode` section).
- **Operations take their shape's width.** The module-wide `owner`/`Wide` of `VectorEmitter` are gone: every `Vector128`/`Vector256` method, `As`, `Zero`, partial load and store, mask conversion and helper is chosen by the shape's `Bits`.
- **Where the widths meet** (`VectorEmitter.Part`): a value is viewed in fragments of the other width, a `Vector128` of a `Vector256` value as `GetLower`/`GetUpper` and a `Vector256` of `Vector128` values as `Create` of two or `ToVector256Unsafe` of one (no instruction; the upper lanes are undefined, as a shorter vector's always were). Casts between lane widths (`Resize`) run in the result's width, so a `<16 x i8>` widened to `<16 x i16>` is one `vpmovsxbw ymm, xmm` and SV-29's `WidenLowest32` widens a `Vector128` of bytes into a `Vector256` of ints in one instruction, while an `<8 x i32>` truncated to `<8 x i8>` packs its two halves as `Vector128`s. Masks of another width (a `<16 x i1>` of 16-bit lanes selecting `<16 x i8>`) convert the same way, `sitofp` of `<4 x i64>` to `<4 x float>` takes the halves of its one `Vector256`, and a `shufflevector` views its operands in the result's width. A 256-bit result of 8- or 16-bit lanes from 128-bit operands (a concatenation or an interleave of two `<16 x i8>`) is gathered as two `Vector128` halves and joined with `vinserti128`, because AVX2 has no byte or word shuffle across the halves of a `Vector256`; 32- and 64-bit lanes stay in 256 bits, where `vpermd`/`vpermq` move them.
- **The target intrinsic helpers** (`Convert`) receive a 128-bit value as the `Vector128` they take, instead of narrowing a `Vector256` to its low half and widening the result back.
- `--report-simd` counts "Vector128 and Vector256 operations" for a 256-bit module.

### Correctness

- `tests/ir/vector-widths.ll` (new; in `ir.sh` at 128 bits and in `vector-lowering.sh` typed at 256 bits, without AVX2 and as NativeAOT at `x86-64-v3`): concatenations, halves, byte, word and dword interleaves, a deinterleave, shuffles across halves, `zext`/`sext`/`trunc` between 128- and 256-bit vectors, `fpext`/`fptrunc`, `sitofp`/`uitofp` of `<4 x i64>` and `<8 x i64>`, `fptosi` to both widths, masks through a phi, from bits, shuffled, extended and of 16-bit lanes selecting bytes, `<24 x i8>` and `<3 x i64>` memory accesses, variable lane indices, reductions and `llvm.vector.extract`/`insert`/`reverse`; its FNV-1a hash of all results is native's (the same at `-O0`, `-O2` and `-march=x86-64-v3`), and changing one input byte changes it.
- `vector-lowering.sh` builds `tests/c/vector-extensions.c` at `-O0`, `-O2` and `-O3` with `--vector-width=256` (before, only `-O2`), with and without AVX, and every `tests/ir/vector-*.ll` typed at 256 bits; outside the gate the extensions also matched native at `-O1` in both modes, without hardware intrinsics.
- `vector-lowering.sh`, `vector-reductions.sh` and `vector-sweep.sh` (with its `-march=x86-64-v3` builds) pass, and so does the core gate (104 tests, `x86-intrinsics.sh` with its `x86-64-v3` helpers among them).

### x264 at x86-64-v3

Static size of the hottest translated functions (`--aot-debug` builds, `tools/fsize.py`: `objdump` of `__managedcode`), instructions with and without the change:

| Function | Before | After | ymm before / after |
| --- | ---: | ---: | ---: |
| `x264_pixel_satd_8x4` | 147 | 141 | 82 / 64 |
| `sub4x4_dct` | 162 | 144 | 107 / 12 |
| `get_ref` | 1,375 | 1,342 | 150 / 100 |
| `hpel_filter` | 1,341 | 1,331 | 429 / 349 |
| `x264_pixel_sad_x4_8x8` | 377 | 267 | 0 / 0 |
| `x264_pixel_satd_4x4` | 199 | 173 | 126 / 0 |
| `sa8d_8x8` | 348 | 333 | 259 / 0 |
| `x264_pixel_sad_16x16` | 204 | 174 | 0 / 0 |
| `x264_pixel_sad_8x8` | 115 | 77 | 0 / 0 |
| `add4x4_idct` | 229 | 203 | 99 / 0 |
| `mc_chroma` | 532 | 504 | 197 / 146 |
| `frame_init_lowres_core` | 875 | 887 | 363 / 288 |
| `x264_slicetype_mb_cost` | 1,902 | 1,918 | 15 / 0 |

The SADs lose a `vmovaps xmm, xmm` after every 8-byte row load (24 in `x264_pixel_sad_8x8`, 96 in `x264_pixel_sad_x4_8x8`): `Vectors.LoadWide` cleared the upper half of the `Vector256` each row was held in, and the rows now load as `Vector128`s, which SV-21's 128-bit `psadbw` takes as they are. The 4x4 transforms and `satd_4x4` run entirely in `xmm` registers.

Seconds per function of the two builds profiled at the same time (`tools/profile-pair.sh`, sampler at 100 µs):

| Function | Before | After |
| --- | ---: | ---: |
| `get_ref` | 3.32 | 3.04 |
| `mc_chroma` | 1.02 | 0.89 |
| `x264_pixel_sad_x4_8x8` | 1.32 | 1.20 |
| `sub4x4_dct` | 0.72 | 0.63 |
| `x264_pixel_sad_16x16` | 0.84 | 0.78 |
| `refine_subpel` | 0.76 | 0.72 |
| sampled total | 26.62 | 25.79 |

The functions that came out slower by up to 0.04 s (`x264_macroblock_tree_propagate`, `x264_pixel_var_16x16`, `mc_copy_w16`) have the same code in both builds.

Paired runs of the release builds (user seconds, medians; both builds at the same time on cores 14 and 15, swapped every round; `artifacts/x264-perf/tools/pair.py`, rows in `results-pair.tsv` there):

| Pair (rounds) | Other build (s) | `x86-64-v3` with SV-41 (s) | With SV-41 / other, per round |
| --- | ---: | ---: | --- |
| `x86-64-v3` before SV-41 (4) | 24.81 | 24.13 | 0.965, 0.960, 0.980, 0.961 (median 0.963) |
| `x86-64-v3 --vector-width=128`, before SV-41 (4) | 26.33 | 23.98 | 0.916, 0.908, 0.911, 0.918 (median 0.913) |
| Vector128, unchanged by SV-41 (2) | 22.70 | 20.94 | 0.918, 0.928 |

So x264 at `x86-64-v3` takes 3.7% less time with fragments sized per value; on the unloaded host, where the build before took 13.55 s against native's 9.81 s, that would be about 13.05 s (1.33 times native), which the coordinator's re-measurement replaces. 128-bit fragments for the whole module, 3% faster than 256-bit ones before SV-24 to SV-30, are now 6% slower than the base and 9.5% slower than this build: the AVX2 paths of SV-24 (per-lane shifts) and SV-29 (widening in one instruction) are for 256-bit values, which the mixed lowering keeps. The Vector128 pair ran later, under less load, so its ratio is not comparable with the others.

## Not done

- A deinterleave of a `Vector256` of 32-bit lanes into a `Vector128` (`<8 x i32>` to its even lanes) is two shuffles of its halves and a blend, where the 256-bit lowering had one `vpermd`; native code has `vextracti128` and `vshufps`. Neither form appears in the profile.
- Masks keep the module's lane widths, so a `<4 x i1>` from a `<4 x i32>` comparison is widened to 64-bit lanes when it is stored (as before); a comparison used only by selects is computed again at the select, at its own width, and costs nothing.
