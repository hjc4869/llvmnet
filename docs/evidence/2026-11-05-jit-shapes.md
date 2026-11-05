# Evidence: switches, constant global addresses and the JIT's limits (2026-11-05)

Host: AMD Ryzen AI MAX+ 395 (Zen 5, 16 cores in two CCDs of 8, 32 threads), Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `113adb3`. Plan: [13-jit-patterns.md](../plan/13-jit-patterns.md) (JP-03 to JP-05, JP-09), after the review in [2026-11-04-jit-patterns.md](2026-11-04-jit-patterns.md). Scripts and logs are in the gitignored `artifacts/jit-patterns/` (`jp02/`, `jp03/`): `sweep.py` times one set of images under several runtime settings, `ab-set.py` translates each benchmark's linked bitcode with several compilers and times all of them under the JIT and as NativeAOT, both alternating the order between rounds. The base compiler is a worktree of `113adb3` (`artifacts/jit-patterns/base-wt`) with the same sysroots. Runs were pinned with `taskset` so that two could share the host: one measurement per CCD, builds on the other CCD's spare threads.

## JP-03: switches

### What changed

An LLVM `switch` on at most 64 bits was a chain of `ldloc; ldc; bne.un` with the target's phi copies after each compare, so dispatch cost one compare per case; RyuJIT rebuilds a switch from such a chain only for equality tests against constants that share one target (`switchrecognition.cpp`). `FunctionEmitter.Switch` now lowers it as LLVM's SelectionDAG does (`SwitchLoweringUtils.cpp`):

- Cases are sorted by the condition's CIL value (zero-extended below 32 bits and from 33 to 63, signed at 32 and 64), and consecutive values with one target merge into ranges.
- A dynamic program over the ranges finds the fewest partitions, as `findJumpTables` does: a partition of at least five ranges whose span is at most four times its cases becomes a CIL `switch` on the condition minus its lowest value, holes going to the default. RyuJIT compiles that to a bounds check and a jump table, or a bit test when the table has two targets. A 64-bit condition is range-checked first, since the CIL `switch` takes a 32-bit index.
- The resulting clusters are found by a binary search, split where the case counts on each side are closest to equal (LLVM balances probabilities, which are proportional to cases without profile data), with at most three clusters compared in a row: a single value by `beq`, a range by `sub` and `ble.un`.
- When the default block is `unreachable`, the last cluster of a run needs no compare.
- Every case branches to one stub per target, which holds that edge's phi copies, instead of a copy per case.
- Conditions wider than 64 bits keep the chain of `WideInteger.Compare` calls.

### Correctness

[tests/semantics/generate.py](../../tests/semantics/generate.py) has a new `switch` module: 84 probes at widths 7, 8, 16, 32, 33, 64 and 128, with dense, negative, wrapping (the top of the unsigned range), sign-boundary, sparse, ranged and mixed case sets, a set with one target for most values (a bit-test shape), sets of three, four and five cases, and an unreachable default. Each target returns its own number, and the cases of one target reach the join block straight from the switch, so the switch's own edges store a phi that depends on the condition. main calls each probe with every case value, its neighbours and edge values: 2,485 calls, which print what the native module prints in system and portable mode under the JIT and as NativeAOT ([tests/semantics.sh](../../tests/semantics.sh)). The test also checks with `LlvmNet.Checks il-opcodes` that five dense probes have a CIL `switch` and three sparse or four-case probes have none. The integer, float and extended modules are byte-identical to before; the switch probes use their own random generator.

RyuJIT's code for the dense `i32` probe is `cmp edi, 11; ja default; jmp [table]`, and for the sparse `i64` probe a binary search of `cmp`/`jge` on 64-bit immediates down to `je` leaves.

### The interpreter kernel

`artifacts/jit-patterns/kernels/interp.c`, the review's 40-case bytecode interpreter, 163,840,000 instructions, medians of 7 alternating runs (`taskset -c 0-7`):

| Program length | Native | JIT, base | JIT, JP-03 | NativeAOT, base | NativeAOT, JP-03 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 16 ops | 0.114 | 0.647 | 0.202 | 0.546 | 0.131 |
| 256 ops | 0.137 | 0.642 | 0.212 | 0.544 | 0.140 |
| 4,096 ops (unpredictable) | 0.866 | 0.615 | 1.081 | 0.517 | 0.944 |

With predictable dispatch the interpreter runs at 1.55 to 1.8 times native under the JIT, which includes about 40 ms of start-up, and at 1.02 to 1.15 as NativeAOT; it was 4.7 and 4 times native. Unpredictable dispatch is slower with a jump table, as native's own is: the chain's compares predict better than one indirect branch on this CPU.

### The minimum table

The review's version built tables of four cases, and almabench lost 2.2%: its time is in musl's `sin`, `cos` and `atan2`, whose `switch (n & 3)` became four-entry tables. almabench's bitcode translated with each minimum, JIT, medians of 9 alternating runs:

| Minimum ranges per table | none (base) | 4 | 5 | 8 |
| --- | ---: | ---: | ---: | ---: |
| almabench | 2.477 | 2.521 | 2.478 | 2.490 |

With five, those switches become two compares each, and almabench is level. LLVM's x86 minimum is four; RyuJIT's table costs more than LLVM's (a relative offset added to the table's base before the jump).

### ffmpeg

The decode harness's bitcode translated by each compiler, one thread, 1080p clips, medians of 7 alternating runs (`taskset -c 8-15`); every image decodes both clips with native's frame hashes:

| Image | H.264 | HEVC 10-bit |
| --- | ---: | ---: |
| JIT, base | 2.753 | 3.192 |
| JIT, JP-03 | 2.813 | 3.191 |
| JIT, JP-03 and JP-04 | 2.756 | 3.186 |
| NativeAOT, base | 1.811 | 2.625 |
| NativeAOT, JP-03 and JP-04 | 1.821 | 2.623 |

Within noise, as in the review: the decoders' hot switches are few. JP-03 also takes 13,820 IL instructions, 3,348 local references and 2,882 blocks out of the image, and leaves its count of methods past a MinOpts threshold at 26 (`ilstats`), so it pushes nothing over the limits.

## The benchmark set (JP-03 and JP-04)

`artifacts/jit-patterns/jp03/ab-set.py`: the linked bitcode of each benchmark from a `scripts/benchmark.py` build of `113adb3` (`artifacts/bench/jp02-images`), translated by the base compiler, by this compiler with `initonly` switched off (JP-03 alone, a temporary switch) and by this compiler, as JIT images and NativeAOT executables; all six timed alternately, medians of 5 (`taskset -c 0-7`, while the tiering sweep ran on the other CCD). Every output matched native's. The images keep the base `runtimeconfig.json`. Seconds:

| Benchmark | JIT base | JIT JP-03 | JIT JP-03+04 | NativeAOT base | NativeAOT JP-03 | NativeAOT JP-03+04 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| malloc | 1.047 | 1.031 | 1.027 | 0.879 | 0.882 | 0.881 |
| fwrite | 0.730 | 0.717 | 0.693 | 0.646 | 0.628 | 0.631 |
| printf | 1.947 | 1.971 | 1.981 | 1.912 | 1.950 | 1.965 |
| atomics | 0.298 | 0.299 | 0.294 | 0.249 | 0.251 | 0.249 |
| mutex | 1.116 | 1.155 | 1.137 | 0.993 | 1.034 | 1.032 |
| calli | 2.100 | 2.112 | 2.134 | 1.897 | 1.921 | 1.898 |
| varargs | 1.206 | 1.223 | 1.247 | 0.882 | 0.902 | 0.898 |
| float80 | 0.333 | 0.333 | 0.332 | 0.166 | 0.163 | 0.163 |
| memcpy | 0.581 | 0.583 | 0.587 | 0.448 | 0.447 | 0.449 |
| alloca | 0.311 | 0.310 | 0.310 | 0.186 | 0.182 | 0.181 |
| callback | 0.410 | 0.417 | 0.412 | 0.337 | 0.336 | 0.339 |
| startup | 0.036 | 0.036 | 0.035 | 0.003 | 0.003 | 0.003 |
| dhrystone | 0.155 | 0.155 | 0.139 | 0.136 | 0.137 | 0.138 |
| linpack | 1.101 | 1.098 | 1.081 | 0.927 | 0.928 | 0.930 |
| richards | 0.481 | 0.501 | 0.456 | 0.436 | 0.428 | 0.425 |
| oourafft | 1.334 | 1.312 | 1.325 | 1.227 | 1.215 | 1.227 |
| ffbench | 0.530 | 0.525 | 0.525 | 0.495 | 0.490 | 0.489 |
| whetstone | 0.676 | 0.675 | 0.677 | 0.508 | 0.505 | 0.508 |
| salsa20 | 2.271 | 2.261 | 2.798 | 2.164 | 2.109 | 2.136 |
| perlin | 1.023 | 1.041 | 1.049 | 1.065 | 1.047 | 1.058 |
| reed-solomon | 1.948 | 1.962 | 1.865 | 1.766 | 1.761 | 1.772 |
| almabench | 2.458 | 2.440 | 2.446 | 2.720 | 2.706 | 2.700 |
| fftbench | 0.975 | 1.009 | 0.981 | 0.829 | 0.818 | 0.819 |
| huffbench | 5.668 | 5.661 | 5.685 | 5.732 | 5.765 | 5.759 |
| lpbench | 1.194 | 1.208 | 1.166 | 1.125 | 1.018 | 1.025 |
| fannkuch | 1.516 | 1.519 | 1.521 | 1.459 | 1.459 | 1.464 |
| recursive | 0.763 | 0.767 | 0.788 | 0.460 | 0.459 | 0.461 |
| heapsort | 1.474 | 1.480 | 1.472 | 1.414 | 1.348 | 1.387 |
| hash | 2.220 | 2.051 | 2.172 | 1.785 | 1.698 | 1.860 |
| lists | 3.033 | 3.031 | 3.018 | 2.959 | 2.939 | 2.937 |
| random | 1.067 | 1.067 | 1.066 | 1.023 | 1.024 | 1.020 |
| queens | 1.152 | 1.149 | 1.142 | 0.994 | 0.992 | 0.991 |
| hash2-cxx | 1.082 | 1.093 | 1.089 | 0.907 | 0.910 | 0.913 |
| stepanov-v1p2 | 2.543 | 2.542 | 2.525 | 2.479 | 2.478 | 2.478 |
| stepanov-abstraction | 1.647 | 1.650 | 1.633 | 1.433 | 1.450 | 1.446 |
| functionobjects | 2.729 | 2.752 | 2.752 | 2.244 | 2.263 | 2.251 |
| olden-bh | 1.136 | 1.144 | 1.147 | 0.620 | 0.618 | 0.609 |
| olden-health | 0.184 | 0.188 | 0.184 | 0.089 | 0.089 | 0.090 |
| olden-treeadd | 0.146 | 0.143 | 0.139 | 0.052 | 0.052 | 0.052 |
| olden-tsp | 0.638 | 0.641 | 0.642 | 0.481 | 0.473 | 0.455 |
| olden-em3d | 1.026 | 1.025 | 1.026 | 0.946 | 0.948 | 0.957 |
| olden-voronoi | 0.465 | 0.464 | 0.461 | 0.179 | 0.179 | 0.178 |
| ptrdist-ks | 0.360 | 0.377 | 0.372 | 0.283 | 0.252 | 0.250 |
| ptrdist-yacr2 | 0.379 | 0.386 | 0.379 | 0.202 | 0.206 | 0.205 |
| ptrdist-ft | 0.443 | 0.437 | 0.432 | 0.332 | 0.325 | 0.327 |
| ptrdist-anagram | 0.440 | 0.439 | 0.434 | 0.448 | 0.404 | 0.406 |
| tramp3d | 0.611 | 0.611 | 0.609 | 0.104 | 0.104 | 0.105 |
| bullet | 2.604 | 2.595 | 2.598 | 1.761 | 1.764 | 1.761 |
| jpeg | 0.074 | 0.075 | 0.074 | 0.005 | 0.004 | 0.004 |
| lame | 0.312 | 0.311 | 0.308 | 0.090 | 0.090 | 0.090 |
| sha | 0.064 | 0.063 | 0.064 | 0.011 | 0.010 | 0.011 |
| susan | 0.072 | 0.072 | 0.070 | 0.015 | 0.016 | 0.015 |
| mafft | 14.855 | 15.137 | 14.965 | 14.928 | 15.041 | 15.087 |
| nbench | 0.997 | 0.996 | 0.986 | 0.686 | 0.700 | 0.690 |
| geometric mean against base (startup excluded) | 1 | 1.002 | 0.998 | 1 | 0.989 | 0.991 |

JP-03 is neutral under the JIT and 1.1% faster as NativeAOT, where the ptrdist programs gain 9 to 12%; JP-04 adds 0.4% under the JIT (dhrystone 10%, richards 9%) and nothing as NativeAOT. The extremes are within the nightly's 25%:

- salsa20, JIT, 23% slower with JP-04: a loop alignment artifact. Its hot loop and `salsa20`'s tier-1 code are the same instructions in both images; the loop's start moves because the cold path before it reads immediates instead of fields. With `DOTNET_JitAlignLoops=0` the two images run 2.455 and 2.458 s, and with JP-02's setting, which ships with it, 2.316 and 2.333 s (9 alternating runs).
- hash, NativeAOT, 10% slower with JP-04 than with JP-03 alone, which ran it 5% faster than the base (so 4% slower than the base with both); `initonly` made no difference to the PF-17 kernel under NativeAOT. Code layout, which moves tight NativeAOT loops by up to 10% with identical CIL ([musttail-select](2026-11-02-musttail-select.md), ptrdist-anagram).

## JP-04: global and constant addresses as `initonly`

### What changed

Global and constant addresses are static `nint` fields that `Program`'s type initializer writes once, before any translated code runs. They are now `initonly` when they are fields of `Program` itself. RyuJIT reads an `initonly` static of an initialized class as a constant in optimized code (`impImportStaticReadOnlyField`, `CEEInfo::getStaticFieldContent`), so a loop over a global array uses an immediate base address instead of loading it again after every store through a pointer (PF-17).

The review's experiment marked every such field. That is unsafe beyond `Program`: past 32,768 fields, they go to `__Statics<n>` holder classes, which have no type initializer, and the runtime counts a class without one as initialized as soon as its statics are allocated (`MethodTable::IsInitedIfStaticDataAllocated`). Code optimized before `Program`'s initializer has written those fields, such as the initializer itself without tiered compilation, would read them as zero. They stay writable, as do thread-local globals' fields, whose getters write each thread's address. `Program`'s fields are folded only once its initializer has finished, because `Program` has one; system mode's constructors, which run inside it, read the fields as ordinary loads. ffmpeg's 15,601 address fields are all on `Program`.

### Correctness

[tests/constant-globals.sh](../../tests/constant-globals.sh) builds [tests/cpp/constant-globals.cpp](../../tests/cpp/constant-globals.cpp) with 33,000 more globals from a generated source, so some address fields are in a holder class (the test checks with `LlvmNet.Checks address-fields` that `Program`'s are `initonly` and the holder's are not). A `__attribute__((constructor))` function and a static object's constructor, both with loops, read global arrays, a global pointer into another, constant strings, the generated globals through a table, and in system mode glibc's `optind` and `stdout`, which the type initializer resolves by symbol; main runs hot loops over the arrays and calls the table sum 200 times. In both modes it prints what the native build prints under the default tiering, `DOTNET_TieredCompilation=0`, `DOTNET_TC_QuickJitForLoops=0` (both optimize the constructors at their first call, before main) and `DOTNET_TC_AggressiveTiering=1`, as NativeAOT, and as a ReadyToRun image from crossgen2 10.0.14 under the same four settings.

### The PF-17 kernel

`artifacts/jit-patterns/kernels/globals.c` (`saxpy` and a histogram over global arrays, 60,000 rounds), medians of 9 alternating runs on `taskset -c 28-31`; "switch" is JP-03 alone:

| | Native | Base | JP-03 | JP-03 and JP-04 |
| --- | ---: | ---: | ---: | ---: |
| JIT | 0.316 | 0.666 | 0.665 | 0.585 |
| NativeAOT | | 0.548 | 0.555 | 0.556 |

`initonly` makes the kernel 12% faster under the JIT, as in the review, and changes nothing under NativeAOT, where ILC cannot run `Program`'s initializer at compile time; PF-17's hoisting remains the fix there.

On the benchmark set, see [the table above](#the-benchmark-set-jp-03-and-jp-04).

## JP-05: the limits report and compiling over-limit methods once

### What changed

- `--diagnose-limits` computes RyuJIT's five MinOpts inputs from the emitted IL as the JIT's importer counts them (`compSetOptimizationLevel` in `compiler.cpp`, `fgFindJumpTargets` and `fgFindBasicBlocks` in `fgbasic.cpp`): IL bytes, IL instructions without prefixes, basic blocks (branch targets, the instruction after a branch, switch, return, throw, `leave` or `endfinally`, and protected-region edges), locals with the arguments, and local references (every `ldarg`, `ldarga`, `starg`, `ldloc`, `ldloca` and `stloc`). It reported IL size, blocks and locals before, the latter two from LLVM's counts, and so missed the instruction and reference triggers (BE-46). Flagged methods are listed first.
- The translated methods past the IL, block or local threshold are marked `AggressiveOptimization` in the image's MethodDef rows (`CilCompiler.CompileOnce`). The tiered JIT compiled such a method with minimal optimization at its first call and again for tier 1, where the recompilation also falls back to MinOpts and the runtime discards it; with the flag it compiles each once. NativeAOT optimizes them fully regardless.

### The instruction and reference thresholds

The review read all five thresholds as applying to every compilation, but the 10.0.14 runtime optimizes some methods past the instruction and reference thresholds: in the H.264 decode `ff_h264_filter_mb` (20,061 instructions, 8,111 references), in an MPEG-2 sample of the FATE suite `mpeg_decode_slice` (23,528 and 11,072), in a VP8 sample `vp8_decode_frame_header`, all `Tier-0 switched to FullOpts`. Synthetic IR methods past only those two thresholds (28,002 instructions and 14,001 references, or 16,002 and 8,001) are MinOpts, at tier 0 as without tiered compilation, so the counts are right and the thresholds are as read. The difference is the switch: a tier-0 compilation of a method with a loop that on-stack replacement cannot patch (one with `localloc`, for example) or with a `tail.` call is switched to full optimization by `fgSwitchToOptimized`, which calls `compInitOptions` and so sets `opts.instrCount` and `opts.lvRefCount` back to zero after `fgFindBasicBlocks` has counted them (`flowgraph.cpp`, `compiler.cpp`; the same in `release/10.0`). Such a method meets only the IL, block and local thresholds. Every other compilation (tier 1, on-stack replacement, `AggressiveOptimization`, tiering off) meets all five.

So the flag is set only for methods past a threshold every compilation checks. A first version that marked all 25 made `ff_h264_filter_mb` MinOpts code, and the H.264 decode was no faster (2.802 s against 2.756). The report gives both counts: 25 methods past a threshold, 16 of them past the IL, block or local threshold. With the 16 marked, the H.264 decode compiles `ff_h264_decode_mb_cabac` and `ff_h264_filter_mb_fast` once and `ff_h264_filter_mb` with full optimization, and takes 2.740 s against 2.789 (9 alternating runs, `taskset -c 8-15`), 1.8% less; the review's 3.7% was for seven such methods, before BE-33.

The finding also matters for the methods it does not change. One past only the instruction or reference threshold that tier 0 compiles without switching (its loops can be patched) gets MinOpts code for its tier-1 and on-stack-replacement compilations, so it never leaves tier 0. Bringing the references down, which JP-05's stack-kept values do, is what helps those.

### ffmpeg's decoder

The decode harness (`artifacts/ffmpeg-browser-portable/decode.bc`), translated by this compiler: 7,538 translated methods, 25 past a threshold, every one of them on local references, 20 on instructions, 16 on IL size and one on blocks (`ff_vp9_decode_block`); none on locals since BE-33. The review's `ilstats` tool finds the same 25 and the type initializer. The largest are VP9's `ff_vp9_decode_block` (128,305 IL bytes, 48,050 instructions, 21,200 references), its scaled inter prediction, AC-3's frame decoder and H.264's CABAC macroblock decoder. JP-03 left the count at 26 with `ilstats` (25 and the type initializer) and removed 13,820 instructions, 3,348 references and 2,882 blocks from the image.

In the H.264 decode, the JIT's summary (`DOTNET_JitDisasmSummary=1`) shows three of the 25 reached: `ff_h264_decode_mb_cabac` and `ff_h264_filter_mb_fast`, past the IL threshold and each compiled twice (`Tier-0 switched to FullOpts, then to MinOpts` or `Tier0`, then `Tier-0 switched MinOpts` for tier 1, which is discarded), and `ff_h264_filter_mb`, past the instruction and reference thresholds and compiled once with full optimization; the review saw seven over-limit methods before BE-33.

## JP-09: MinOpts methods in the benchmark report

`scripts/benchmark.py` runs each JIT engine's validation with `DOTNET_JitDisasmSummary=1` and `DOTNET_JitStdOutFile`, lists the translated methods (of the image's `Program` classes) the summary shows compiled with MinOpts in `minopts.tsv`, and counts them per benchmark in `results.tsv` and the summary. The baseline has a `minopts` column, and a JIT row with more such methods than the baseline's fails the run. [tests/benchmarks.sh](../../tests/benchmarks.sh) checks the comparison on synthetic results: as many or fewer pass, one more fails.
