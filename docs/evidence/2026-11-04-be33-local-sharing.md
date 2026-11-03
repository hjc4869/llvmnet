# Evidence: BE-33, IL locals shared by liveness (2026-11-04)

Host: AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB, Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `ee9304b`. Found by SV-05's vectorized benchmark run ([evidence](2026-11-03-vector-lowering.md#sv-05-autovectorization)).

## The cause

The function emitter gave every LLVM value its own IL local, except in functions of more than 32,768 values, which reused locals within a block. RyuJIT has two limits on locals that matter here, both fixed in the release runtime:

- It tracks at most 1,024 locals of a method (`JitMaxLocalsToTrack`); the others live in the stack frame and are never enregistered.
- It stops inlining in a method of about 920 locals, 90% of that. A probe of dynamic methods with 100 calls of `Vector128.Add<double>` inlines every call with 880 `long` and 20 vector locals and none with 900 and 20 (and none with 600 and 400, all with 600 and 300).

A scalar function crosses those lines at about a thousand LLVM instructions: musl's `printf_core` had 1,421 locals and `__floatscan` 822, so every C program's formatted output ran partly from memory. Vectorized code crosses them sooner, because the loop vectorizer adds vector bodies, epilogues and run-time checks. Stepanov's abstraction benchmark has 721 locals in `main` scalar and 1,499 vectorized, and the vectorized build ran 12 to 17 s against 1.74 s; with `DOTNET_JitMaxLocalsToTrack=0x1000` it took 1.67 s. In .NET 10, `Vector128.Add<T>` and the other element-wise methods reach RyuJIT's intrinsic expansion only through inlining, so in oourafft's `cftmdl` (1,201 locals) 500 of them were calls (SV-05 now emits the operators, which RyuJIT expands itself).

## The change

`FunctionEmitter.ShareFunctionLocals` gives values of one CIL type the same local when their live ranges do not meet. A live range is the span, in emission order, of the value's definition, its uses and the blocks it is live into or out of. An iterative liveness pass computes those blocks over bit sets of the values used outside their defining block. A phi's range starts at the predecessor edges that store it. Ranges that share a position conflict, so an instruction's result never takes the local of one of its operands, because some instructions store part of their result before they have read every operand. The emitter reads the operands of comparisons, shuffles and insertelements where those are used (the fused compare-and-select, mask recomputation, splat shift counts, insertelement chains), so those operands count as used there too. A linear scan over the ranges assigns the locals.

Only functions of more than 512 values share. RyuJIT allocates registers per local, not per value, so values that share a local share one allocation, and the copies at phis then need moves: with sharing everywhere, heapsort's sift-down loop gained register-to-register moves and lpbench, heapsort, ptrdist-anagram and the mutex benchmark ran 6 to 15% slower under both engines (A/B below). A function of at most 512 values keeps a local per value as before, which leaves room for RyuJIT's own temporaries below the inlining limit.

Functions with `setjmp` keep a local per value, because the jump dispatch re-enters the body at its call sites. Functions of more than 32,768 values keep the per-block scheme. Value locals are only loaded and stored; the temporaries whose address is taken are declared separately and never shared.

## Locals and IL size

`--diagnose-limits` on ffmpeg's portable decoder (7,531 methods), before and after:

| | before | after |
| --- | ---: | ---: |
| IL bytes | 22,991,289 | 18,236,708 |
| methods past a MinOpts threshold | 38 | 16 |
| methods past the 2,000-locals threshold | 38 | 0 |
| `ff_h264_decode_mb_cabac` | 169,077 bytes, 6,505 locals | 105,368 bytes, 97 locals |
| `ff_h264_filter_mb_fast` | 151,235 bytes, 5,242 locals | 103,458 bytes, 239 locals |
| `ff_vp9_decode_block` | 206,946 bytes, 7,566 locals | 128,143 bytes, 196 locals |

IL shrinks by a fifth because most locals now take short-form indices. The 16 methods left are past the 60,000-byte IL threshold and still compile as MinOpts under the JIT, so the denser IL this item also asks for (and BE-26's splitting) is still needed for them. In the benchmark set, `printf_core` has 101 locals (from 1,421), Stepanov's vectorized `main` 43 (from 1,499) and oourafft's `cftmdl` 152 (from 1,201).

## Speed

The same linked bitcode of the benchmarks that moved most, translated by the compiler at `ee9304b` (before) and with sharing, `scripts/ab-time.py 7` (alternating order), median seconds, and the after/before ratio with sharing in every function and with the 512-value threshold:

| benchmark | JIT before | sharing everywhere | more than 512 values | NativeAOT before | sharing everywhere | more than 512 values |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| atomics | 0.314 | 0.97 | 0.97 | 0.286 | 1.02 | 0.99 |
| heapsort | 1.390 | 1.09 | 1.06 | 1.437 | 1.12 | 1.05 |
| lpbench | 1.286 | 1.15 | 1.06 | 1.071 | 1.14 | 1.04 |
| mutex | 1.195 | 1.09 | 0.96 | 1.095 | 0.96 | 0.98 |
| olden-em3d | 1.047 | 0.99 | 1.00 | 0.941 | 1.63 | 1.04 |
| ptrdist-anagram | 0.443 | 1.10 | 1.00 | 0.450 | 1.06 | 1.00 |
| ptrdist-yacr2 | 0.396 | 0.97 | 1.14 | 0.205 | 1.03 | 1.01 |
| richards | 0.485 | 0.98 | 1.01 | 0.430 | 1.05 | 1.01 |
| tramp3d | 0.613 | 0.98 | 1.13 | 0.117 | 0.97 | 0.90 |

With the threshold, heapsort's and lpbench's hot functions have the same IL as before (only `printf_core`, `alloc_slot` and the startup method differ in heapsort's image), so what remains of their 4 to 6% is the placement of unchanged code, as are yacr2's and tramp3d's swings between the two rounds.

olden-em3d as NativeAOT was 1.6 times slower with sharing everywhere because of placement alone. Its hot loop, a duplicate check in `make_neighbors` that runs about 10^9 times, is the same seven instructions in both versions: RyuJIT keeps the loop bound in the stack frame and reloads and stores it back on every iteration. A few bytes more before it moved the loop across a 32-byte boundary, which takes it from 0.95 to 1.5 s; the JIT's full-optimization code (`DOTNET_TieredCompilation=0`) shows the same, 1.03 against 1.51 s, and is back at 1.04 s with `DOTNET_JitAlignLoops=0`. The tiered JIT recompiles the loop and was unaffected. The spill is filed as PF-20; with the threshold, `make_neighbors` keeps its locals and its loop its place.

The benchmark set with `scripts/benchmark.py --vectorized --repeats 3`, the final compiler against the first run (compiler `ee9304b`), host-normalized geometric means of the fastest runs: the JIT 0.980 and NativeAOT 0.987 for the scalar builds (56 programs), 0.894 and 0.873 for the vectorized ones (52). ffmpeg's H.264 decoder, whose hot functions had thousands of locals, is 10 to 17% faster (one and four threads, both engines); against `tests/perf/baseline.tsv` the scalar engines stand at 0.947 and 0.937 with no benchmark past its threshold. Stepanov's vectorized `main` (1,499 locals before, 43 after) went from 12 to 17 s to 1.73 s, oourafft's `cftmdl` from 1,201 locals to 152.

## Correctness

The core and toolchain gate passes 109 of 109 tests. The vector tests (`vector-lowering`, `vector-semantics`, `vector-sweep`, `vectors`, `simd128`, `wasm-simd128`) pass, and the 58 programs of the benchmark set match native output in all six engines, with sharing everywhere and with the threshold.
