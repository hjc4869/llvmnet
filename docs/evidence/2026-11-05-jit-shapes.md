# Evidence: tiering, switches, constant global addresses and the JIT's limits (2026-11-05)

Host: AMD Ryzen AI MAX+ 395 (Zen 5, 16 cores in two CCDs of 8, 32 threads), Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `113adb3`. Plan: [13-jit-patterns.md](../plan/13-jit-patterns.md) (JP-02 to JP-05), after the review in [2026-11-04-jit-patterns.md](2026-11-04-jit-patterns.md). Scripts and logs are in the gitignored `artifacts/jit-patterns/` (`jp02/`, `jp03/`): `sweep.py` times one set of images under several runtime settings, `ab-set.py` translates each benchmark's linked bitcode with several compilers and times all of them under the JIT and as NativeAOT, both alternating the order between rounds. The base compiler is a worktree of `113adb3` (`artifacts/jit-patterns/base-wt`) with the same sysroots. Runs were pinned with `taskset` so that two could share the host: one measurement per CCD, builds on the other CCD's spare threads.

## JP-02: the tiering delay

### What changed

The emitted `runtimeconfig.json` sets `System.Runtime.TieredCompilation.CallCountingDelayMs` to 0, beside `System.Runtime.TieredPGO: false`. By default the runtime starts counting calls toward tier 1 only after 100 ms in which no method is called for the first time (`TieredCompilationManager`), and translated programs keep calling functions for the first time, so their call-heavy code stayed at tier 0. With 0, call counting starts at a method's first call. `DOTNET_TC_CallCountingDelayMs`, which the runtime reads before the property, restores the default for a run: lame's image takes 0.204 s with the emitted setting and 0.369 s with `DOTNET_TC_CallCountingDelayMs=100`, against 0.344 for the image without the property (5 runs each, during a gate run). `CallCountThreshold` stays at the default 30.

System-mode programs that fork keep the default. A later gate run found `tests/system-fork.sh` timing out: with the delay at 0, a child forked under `DOTNET_EnableWriteXorExecute=0` (the JIT's only mode with a working `fork`) inherits the locks the runtime's background thread holds while it compiles for tier 1, and waits on one forever (`futex_wait_queue`). The fixture's JIT run with private code left a hung child in 2 of 20 runs with the delay at 0 and in none of 20 with `DOTNET_TC_CallCountingDelayMs=100`. So the compiler leaves the property out of system-mode images that call `fork`, `vfork`, `_Fork`, `forkpty` or `daemon` (0 of 30 runs hung), and `tests/system-fork.sh` checks that it does. A program that forks later, after tier-up has begun, can still meet the same hazard with the default; it was there before.

### The sweep

`artifacts/jit-patterns/jp02/sweep.py` timed the JIT images of one build of the whole set (`scripts/benchmark.py --validate-only --engines native,jit` at `113adb3`, `artifacts/bench/jp02-images`, all outputs matching native) under each setting, passed as `DOTNET_TC_*` environment variables, alternating the candidates in rotated order, 5 rounds each, with outputs checked against the first candidate's. Once on all 32 threads with the host otherwise idle, and once on 4 cores (`taskset -c 8-11`, the CI runner's size) while other measurements ran on the other CCD; the 4-core ffmpeg rows were measured again alone, because builds had shared those cores. Medians in seconds:

| Benchmark | 32 threads: default | 0 | 1 | 0, threshold 10 | 0, threshold 100 | 4 cores: default | 0 | 1 | 10 | 30 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| malloc | 1.075 | 0.942 | 0.997 | 0.952 | 0.955 | 1.092 | 0.997 | 0.992 | 1.026 | 1.035 |
| fwrite | 0.726 | 0.739 | 0.784 | 0.731 | 0.708 | 0.766 | 0.749 | 0.741 | 0.762 | 0.742 |
| printf | 1.989 | 1.840 | 1.897 | 1.839 | 1.835 | 2.098 | 1.963 | 1.974 | 1.969 | 2.007 |
| atomics | 0.310 | 0.304 | 0.318 | 0.303 | 0.307 | 0.310 | 0.317 | 0.320 | 0.326 | 0.325 |
| mutex | 1.252 | 1.212 | 1.189 | 1.221 | 1.191 | 1.162 | 1.089 | 1.195 | 1.089 | 1.128 |
| calli | 2.183 | 2.235 | 2.104 | 2.159 | 2.083 | 2.270 | 2.235 | 2.197 | 2.217 | 2.264 |
| varargs | 1.211 | 1.010 | 1.016 | 1.013 | 1.008 | 1.276 | 1.070 | 1.074 | 1.086 | 1.154 |
| float80 | 0.338 | 0.194 | 0.196 | 0.198 | 0.184 | 0.347 | 0.195 | 0.197 | 0.228 | 0.266 |
| memcpy | 0.591 | 0.553 | 0.554 | 0.556 | 0.549 | 0.625 | 0.597 | 0.584 | 0.600 | 0.617 |
| alloca | 0.315 | 0.261 | 0.256 | 0.256 | 0.256 | 0.327 | 0.274 | 0.273 | 0.278 | 0.291 |
| callback | 0.431 | 0.396 | 0.394 | 0.419 | 0.417 | 0.443 | 0.421 | 0.421 | 0.421 | 0.428 |
| startup | 0.036 | 0.034 | 0.034 | 0.034 | 0.034 | 0.036 | 0.035 | 0.036 | 0.035 | 0.037 |
| dhrystone | 0.154 | 0.155 | 0.156 | 0.151 | 0.160 | 0.168 | 0.165 | 0.166 | 0.166 | 0.166 |
| linpack | 1.097 | 1.003 | 1.006 | 0.995 | 1.027 | 1.188 | 1.087 | 1.087 | 1.098 | 1.119 |
| richards | 0.489 | 0.469 | 0.468 | 0.461 | 0.469 | 0.515 | 0.506 | 0.500 | 0.496 | 0.506 |
| oourafft | 1.263 | 1.147 | 1.149 | 1.135 | 1.159 | 1.363 | 1.237 | 1.222 | 1.249 | 1.280 |
| ffbench | 0.529 | 0.536 | 0.535 | 0.545 | 0.509 | 0.559 | 0.578 | 0.573 | 0.576 | 0.573 |
| whetstone | 0.676 | 0.520 | 0.517 | 0.518 | 0.505 | 0.700 | 0.536 | 0.542 | 0.581 | 0.643 |
| salsa20 | 2.237 | 2.097 | 2.117 | 2.117 | 2.095 | 2.416 | 2.291 | 2.298 | 2.319 | 2.351 |
| perlin | 1.006 | 1.006 | 1.004 | 1.002 | 0.998 | 1.070 | 1.065 | 1.065 | 1.075 | 1.067 |
| reed-solomon | 1.941 | 1.904 | 1.922 | 1.899 | 1.915 | 2.116 | 2.033 | 2.036 | 2.029 | 2.050 |
| almabench | 2.447 | 2.353 | 2.341 | 2.327 | 2.323 | 2.770 | 2.661 | 2.660 | 2.667 | 2.683 |
| fftbench | 1.020 | 0.996 | 0.998 | 0.997 | 0.989 | 1.000 | 1.056 | 1.030 | 1.062 | 0.991 |
| huffbench | 5.613 | 5.620 | 5.614 | 5.670 | 5.641 | 6.144 | 6.171 | 6.287 | 6.326 | 6.235 |
| lpbench | 1.119 | 1.116 | 1.089 | 1.129 | 1.105 | 1.373 | 1.365 | 1.569 | 1.489 | 1.491 |
| fannkuch | 1.464 | 1.453 | 1.461 | 1.464 | 1.463 | 1.597 | 1.583 | 1.597 | 1.595 | 1.610 |
| recursive | 0.761 | 0.542 | 0.536 | 0.525 | 0.532 | 0.793 | 0.563 | 0.560 | 0.590 | 0.680 |
| heapsort | 1.375 | 1.371 | 1.391 | 1.642 | 1.395 | 1.524 | 1.498 | 1.455 | 1.569 | 1.630 |
| hash | 1.969 | 1.807 | 1.801 | 1.788 | 1.802 | 2.287 | 2.102 | 2.110 | 2.224 | 2.213 |
| lists | 3.005 | 2.939 | 2.934 | 2.917 | 2.905 | 3.474 | 3.503 | 3.489 | 3.586 | 3.528 |
| random | 1.081 | 1.067 | 1.070 | 1.070 | 1.072 | 1.229 | 1.246 | 1.239 | 1.241 | 1.258 |
| queens | 1.146 | 1.098 | 1.070 | 1.088 | 1.083 | 1.233 | 1.163 | 1.164 | 1.178 | 1.192 |
| hash2-cxx | 1.092 | 1.008 | 1.024 | 1.016 | 1.008 | 1.172 | 1.099 | 1.107 | 1.112 | 1.138 |
| stepanov-v1p2 | 2.552 | 2.545 | 2.549 | 2.531 | 2.535 | 2.793 | 2.774 | 2.805 | 2.781 | 2.789 |
| stepanov-abstraction | 1.657 | 1.495 | 1.503 | 1.501 | 1.503 | 1.804 | 1.671 | 1.653 | 1.701 | 1.804 |
| functionobjects | 2.767 | 2.219 | 2.225 | 2.224 | 2.226 | 3.549 | 2.995 | 3.021 | 2.670 | 3.274 |
| olden-bh | 1.138 | 1.077 | 1.084 | 1.082 | 1.106 | 1.247 | 1.184 | 1.182 | 1.188 | 1.249 |
| olden-health | 0.200 | 0.146 | 0.152 | 0.173 | 0.153 | 0.194 | 0.155 | 0.161 | 0.179 | 0.196 |
| olden-treeadd | 0.151 | 0.102 | 0.112 | 0.104 | 0.112 | 0.160 | 0.116 | 0.120 | 0.133 | 0.148 |
| olden-tsp | 0.669 | 0.519 | 0.538 | 0.543 | 0.511 | 0.694 | 0.603 | 0.620 | 0.645 | 0.702 |
| olden-em3d | 1.033 | 1.038 | 1.036 | 1.039 | 1.036 | 1.128 | 1.112 | 1.107 | 1.116 | 1.111 |
| olden-voronoi | 0.465 | 0.229 | 0.236 | 0.224 | 0.233 | 0.491 | 0.243 | 0.253 | 0.310 | 0.391 |
| ptrdist-ks | 0.362 | 0.311 | 0.321 | 0.316 | 0.326 | 0.386 | 0.342 | 0.337 | 0.358 | 0.366 |
| ptrdist-yacr2 | 0.396 | 0.304 | 0.323 | 0.306 | 0.327 | 0.411 | 0.323 | 0.329 | 0.362 | 0.402 |
| ptrdist-ft | 0.518 | 0.488 | 0.536 | 0.457 | 0.422 | 0.475 | 0.427 | 0.430 | 0.456 | 0.479 |
| ptrdist-anagram | 0.456 | 0.445 | 0.452 | 0.449 | 0.452 | 0.482 | 0.484 | 0.473 | 0.475 | 0.477 |
| tramp3d | 0.692 | 0.500 | 0.529 | 0.503 | 0.497 | 0.746 | 0.600 | 0.625 | 0.678 | 0.718 |
| bullet | 2.633 | 1.924 | 1.917 | 1.927 | 1.942 | 2.620 | 2.062 | 2.080 | 2.235 | 2.441 |
| jpeg | 0.076 | 0.082 | 0.081 | 0.074 | 0.086 | 0.080 | 0.078 | 0.080 | 0.080 | 0.080 |
| lame | 0.325 | 0.178 | 0.179 | 0.174 | 0.194 | 0.327 | 0.180 | 0.188 | 0.203 | 0.225 |
| sha | 0.066 | 0.066 | 0.065 | 0.062 | 0.063 | 0.068 | 0.066 | 0.067 | 0.066 | 0.067 |
| susan | 0.073 | 0.073 | 0.075 | 0.076 | 0.072 | 0.075 | 0.074 | 0.074 | 0.074 | 0.075 |
| mafft | 14.783 | 14.655 | 14.614 | 14.961 | 15.091 | 16.004 | 15.875 | 15.894 | 15.859 | 15.989 |
| nbench | 0.989 | 0.649 | 0.658 | 0.654 | 0.651 | 1.046 | 0.708 | 0.712 | 0.805 | 0.925 |
| ffmpeg-hevc-1t | 2.901 | 2.711 | 2.754 | 2.714 | 2.729 | 3.278 | 3.106 | 3.149 | 3.173 | 3.232 |
| ffmpeg-hevc-4t | 1.403 | 1.236 | 1.275 | 1.247 | 1.272 | 1.582 | 1.400 | 1.423 | 1.489 | 1.516 |
| ffmpeg-h264-1t | 2.389 | 2.137 | 2.148 | 2.161 | 2.155 | 2.757 | 2.495 | 2.469 | 2.551 | 2.635 |
| ffmpeg-h264-4t | 1.159 | 0.909 | 0.931 | 0.908 | 0.914 | 1.334 | 1.114 | 1.142 | 1.198 | 1.243 |
| geometric mean against the default (startup excluded) | 1 | 0.881 | 0.892 | 0.885 | 0.883 | 1 | 0.892 | 0.900 | 0.925 | 0.961 |

- **Delay 0** is the best candidate in both configurations: 12% less time on all threads and 11% on 4 cores, by geometric mean. The largest gains are the call-heavy and start-up-bound programs (olden-voronoi and lame about twice as fast, float80, nbench, recursive, tramp3d, bullet, functionobjects 20 to 45% faster) and every ffmpeg decode, by 7% (HEVC, one thread) to 22% (H.264, four threads) on all threads.
- **A small delay** (1, 10 or 30 ms) only gives part of the gain: the delay re-arms after each first call, and these programs make one every few milliseconds.
- **The call-count threshold** with no delay is no better: 10 makes heapsort 19% slower (its hot function promoted before its loop settles), 100 jpeg 13%.
- **The losers** are small and within the nightly's thresholds. On all threads jpeg is 8% slower (6 ms of an 80 ms run; level on 4 cores), calli 2.4%, fwrite 1.8%; on 4 cores fftbench 5.6%, ffbench 3.3%, atomics 2.1%. The startup probe takes 34 ms against 36 ms, sha and susan are level, and dhrystone and heapsort, which the review saw 6 to 9% slower on 22 benchmarks, are level here.


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

`artifacts/jit-patterns/jp03/ab-set.py`: the linked bitcode of each benchmark from a `scripts/benchmark.py` build of `113adb3` (`artifacts/bench/jp02-images`), translated by the base compiler, by this compiler with `initonly` switched off (JP-03 alone, a temporary switch) and by this compiler, as JIT images and NativeAOT executables; all six timed alternately, medians of 5 (`taskset -c 0-7`, while the tiering sweep ran on the other CCD). Every output matched native's. The images keep the base `runtimeconfig.json` (JP-02 is measured apart). Seconds:

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

## JP-05: values computed where they are used

### What changed

Every LLVM value used to get a local: the instruction computed it, `stloc` stored it, and each use loaded it with `ldloc`. `FunctionEmitter.ChooseUseSiteValues` now picks the values computed where their one use loads them instead, as LLVM's WebAssembly backend keeps such a value on the stack (`WebAssemblyRegStackify`); each saves a `stloc`, an `ldloc` and a local. A value qualifies when:

- its one use is a later instruction of its block whose lowering loads each operand exactly once: a binary operation, a cast, a GEP, a load's address, a store or a `ret` (not comparisons, selects, calls or switches, whose lowerings load operands more than once or keep them in scratch locals);
- its own lowering computes only on the evaluation stack: scalar arithmetic except division and remainder, whose traps would move, casts, GEPs and plain (non-volatile, non-atomic) loads;
- for a load, nothing between it and where it ends up computed may write memory or have another effect (stores, calls, atomics, fences, allocas);
- the chain of such values above it is shorter than 16: an unrolled reduction of 3,438 additions in the AVX intrinsics test overflowed the compiler's stack before this limit.

Functions with `setjmp` sites and functions of more than 32,768 values keep their locals. The local-sharing pass counts a computed value's operands as used where it is computed, and gives it no local.

### Correctness

The semantics probes (47,795 calls; most probes are an operation whose one use is the `ret`), the core and toolchain gate (114 tests), csmith and YARPGen seeds 7001 to 7200 (326 programs matched native in both modes, the others skipped by the fuzzer's limits or rejected for `i144` and `i152`), c-testsuite and libc-test pass. ffmpeg's decodes keep native's frame hashes.

### ffmpeg's decoder

The decode harness translated with the change off (a temporary switch) and on:

| | Off | On |
| --- | ---: | ---: |
| IL bytes | 19,041,371 | 16,211,864 |
| IL instructions (`ilstats`) | 7,661,861 | 6,243,829 |
| local references | 3,081,343 | 1,988,993 |
| locals | 649,484 | 323,633 |
| translated methods past a MinOpts threshold | 25 | 17 |
| of them past the IL, block or local threshold | 16 | 14 |

Eight methods come under every threshold: `ff_h264_filter_mb`, `mpeg_decode_slice`, `vp3_decode_frame`, `vp8_decode_frame_header`, `ff_mpeg4_parse_picture_header`, `hl_decode_mb_444_complex` and two `decode_frame` instances. The 17 that remain, BE-26's list (IL bytes, instructions, blocks, locals, references):

| Method | IL bytes | Instructions | Blocks | Locals | References | Past |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `ff_vp9_decode_block` | 116551 | 42334 | 2849 | 203 | 15484 | il,instructions,blocks,references |
| `inter_pred_scaled_16bpp` | 98070 | 40875 | 1252 | 85 | 14936 | il,instructions,references |
| `inter_pred_scaled_8bpp` | 97007 | 40435 | 1252 | 85 | 14796 | il,instructions,references |
| `vp8_decode_mb_row_no_filter` | 91085 | 37715 | 1771 | 288 | 13075 | il,instructions,references |
| `ff_h264_decode_mb_cabac` | 91056 | 35209 | 1301 | 96 | 10082 | il,instructions,references |
| `ff_h264_filter_mb_fast` | 90980 | 32098 | 1019 | 246 | 8072 | il,instructions,references |
| `ac3_decode_frame` | 90467 | 37277 | 1716 | 312 | 9685 | il,instructions,references |
| `decode_frame_header` | 74790 | 31021 | 1691 | 65 | 12706 | il,instructions,references |
| `decode_frame.4253` | 74138 | 26683 | 1985 | 234 | 9249 | il,instructions,references |
| `ff_h264_decode_mb_cavlc` | 67472 | 26321 | 1247 | 85 | 8386 | il,instructions,references |
| `decode_b` | 66870 | 23771 | 1407 | 113 | 6972 | il,instructions |
| `inter_pred_16bpp` | 66841 | 28210 | 748 | 73 | 10193 | il,instructions,references |
| `inter_pred_8bpp` | 65769 | 27696 | 748 | 73 | 10029 | il,instructions,references |
| `vc1_decode_b_blocks` | 64035 | 27051 | 1357 | 190 | 9710 | il,instructions,references |
| `mp_decode_frame` | 54811 | 22197 | 1178 | 111 | 8826 | instructions,references |
| `vc1_decode_p_blocks` | 53870 | 22680 | 1255 | 166 | 8175 | instructions,references |
| `ff_vfscanf` | 43286 | 16427 | 1465 | 146 | 8136 | references |

### Timing

ffmpeg's decodes, one thread, medians of 7 alternating runs on all threads; native's hashes in every build:

| Image | H.264 | HEVC 10-bit |
| --- | ---: | ---: |
| JIT, off | 2.467 | 3.127 |
| JIT, on | 2.367 | 3.152 |
| NativeAOT, off | 1.934 | 2.832 |
| NativeAOT, on | 1.897 | 2.764 |

H.264 is 4% faster under the JIT and 2% as NativeAOT, HEVC level under the JIT and 2.4% faster as NativeAOT.

The benchmark set (`ab-set.py`, the change off and on, 5 rounds, `taskset -c 0-7`): geometric means 0.998 under the JIT and 0.992 as NativeAOT, every output matching native. The extremes, again with 9 rounds: olden-tsp 8.5% faster under the JIT (0.565 to 0.517 s), tramp3d 7% (0.473 to 0.439); mafft 4.7% slower under the JIT (14.61 to 15.30 s) and 3.5% as NativeAOT. With loads left in their locals mafft takes 15.12 s, so neither kind of value alone explains it; it is within the nightly's thresholds and is the cost of the change on this set. olden-voronoi's 7% in the first run was noise (0.224 and 0.221 s with 9 rounds).

### The census

Translated methods past a MinOpts threshold (`ilstats`, which counts as RyuJIT does), with the change off and on:

| Program | Off | On |
| --- | ---: | ---: |
| ffmpeg's decoder | 25 (16 past IL, blocks or locals) | 17 (14) |
| tramp3d | 3: `main`, `Adv5::X::advect`, `Adv5::Y::advect` | 1: `main` (69,873 IL bytes) |
| mafft | 1: `constants` | 1: `constants` (124,657 IL bytes) |
| the set's other 52 programs (the ffmpeg entries are the decoder above) | 0 | 0 |

The baseline's `minopts` column now records tramp3d's 1, so the nightly fails if the count rises again.

## JP-06: ReadyToRun for one runtime identifier

### What changed

`--readytorun <rid>` (linux-x64, linux-arm64, win-x64, win-arm64, osx-x64, osx-arm64; system mode linux-x64) precompiles a JIT image, the `.dll` or a launcher's payload (`Toolchain.ReadyToRun`). The driver restores a generated project with `PublishReadyToRun`, which brings crossgen2 for this host and the target's runtime pack (from NuGet, the package cache or `LLVMNET_NUGET_FEED`; `scripts/nativeaot-feed.sh` now adds them to the feed), reads their folders from `project.assets.json`, and runs crossgen2 on the image and `LlvmNet.Runtime.dll` with `--composite`, the runtime pack's assemblies and any other assemblies beside the image as references. The output is the two rewritten assemblies and `<name>.r2r.dll`, their code.

- **No shared version bubble.** The plan asked for `LlvmNet.Runtime` in the image's version bubble, so that its helpers inline into precompiled code. With `--inputbubble`, the composite records the framework assemblies' builds: compiled against the 10.0.13 runtime pack that the restore picks, `hello.dll` stopped at start on this host's 10.0.14 (`MVID mismatch between loaded assembly 'System.Private.CoreLib' ... and an assembly with the same simple name embedded in the native image`), so every patch release of .NET would break such images. Without it each assembly keeps its own bubble, and the images run on other patch releases: the win-x64 build of a test program runs its precompiled code on fluorite's 10.0.10 (80 methods jitted against 184 for the IL image), the osx-arm64 build on mainmac's 10.0.14 (67 against 155). The bubble was not worth it anyway (medians of 7 to 9 runs, seconds):

| Program | IL | Composite, no bubble | Shared bubble (`--inputbubbleref`) | Two separate images |
| --- | ---: | ---: | ---: | ---: |
| dhrystone | 0.154 | 0.171 | 0.172 | 0.170 |
| memcpy | 0.560 | 0.572 | 0.581 | 0.580 |
| float80 | 0.195 | 0.171 | 0.182 | 0.186 |

  Hot methods are recompiled at tier 1, which inlines across assemblies, so the bubble only matters for code that stays precompiled.
- **No `AggressiveOptimization`.** crossgen2 leaves methods with that flag to the JIT (`ShouldSkipCompilation`), so JP-05's marking would exclude exactly the over-limit methods; `--readytorun` images do without it.

### The H.264 decode

With tiered compilation off (`DOTNET_TieredCompilation=0`), the ReadyToRun decode jits 58 methods and none with MinOpts: `ff_h264_decode_mb_cabac` and `ff_h264_filter_mb_fast` run crossgen2's code (53,886 bytes for the first, which the JIT compiles to 99,223 bytes of MinOpts code). With tiering on, the summary still shows one MinOpts compilation of each: their tier-1 recompilation, after about two million calls each, falls back to MinOpts and the runtime discards it, keeping the precompiled code (a call-count threshold of 65,535 does not prevent it, as both functions run once per macroblock). JP-09's count reports those two for the ReadyToRun engine as for the JIT.

### The benchmark set

`scripts/benchmark.py --engines native,jit,jit-r2r` (`artifacts/bench/jp06-r2r`, the JP-05 commit's tree, JP-02's setting in both JIT engines, medians of 5; `jit-r2r` is the harness's new engine). jpeg and stepanov-v1p2 are missing: their validation runs crashed with the JIT summary written to a file (JP-09 below), and both match native in 20 runs of each engine without it. Seconds:

| Benchmark | Native | JIT | JIT ReadyToRun | ReadyToRun / JIT |
| --- | ---: | ---: | ---: | ---: |
| malloc | 1.050 | 0.878 | 0.865 | 0.99 |
| fwrite | 0.509 | 0.716 | 0.677 | 0.95 |
| printf | 0.925 | 1.818 | 1.768 | 0.97 |
| atomics | 0.253 | 0.304 | 0.308 | 1.01 |
| mutex | 0.827 | 1.173 | 1.147 | 0.98 |
| calli | 1.486 | 2.131 | 2.059 | 0.97 |
| varargs | 0.352 | 1.016 | 0.993 | 0.98 |
| float80 | 0.006 | 0.193 | 0.174 | 0.90 |
| memcpy | 0.284 | 0.558 | 0.592 | 1.06 |
| alloca | 0.204 | 0.267 | 0.244 | 0.91 |
| callback | 0.337 | 0.423 | 0.448 | 1.06 |
| startup | 0.001 | 0.040 | 0.032 | 0.82 |
| dhrystone | 0.085 | 0.149 | 0.165 | 1.11 |
| linpack | 0.913 | 1.015 | 0.999 | 0.98 |
| richards | 0.348 | 0.455 | 0.439 | 0.96 |
| oourafft | 1.042 | 1.169 | 1.123 | 0.96 |
| ffbench | 0.536 | 0.539 | 0.528 | 0.98 |
| whetstone | 0.445 | 0.529 | 0.514 | 0.97 |
| salsa20 | 2.018 | 2.260 | 2.127 | 0.94 |
| perlin | 1.001 | 0.995 | 0.984 | 0.99 |
| reed-solomon | 1.527 | 1.851 | 1.852 | 1.00 |
| almabench | 2.416 | 2.354 | 2.327 | 0.99 |
| fftbench | 1.219 | 0.978 | 0.816 | 0.83 |
| huffbench | 4.512 | 5.714 | 5.656 | 0.99 |
| lpbench | 1.091 | 1.442 | 1.264 | 0.88 |
| fannkuch | 1.381 | 1.465 | 1.548 | 1.06 |
| recursive | 0.365 | 0.525 | 0.522 | 0.99 |
| heapsort | 1.767 | 1.674 | 1.637 | 0.98 |
| hash | 1.363 | 1.978 | 1.956 | 0.99 |
| lists | 1.645 | 2.967 | 2.947 | 0.99 |
| random | 0.996 | 1.071 | 1.077 | 1.01 |
| queens | 1.090 | 1.133 | 1.091 | 0.96 |
| hash2-cxx | 0.661 | 1.028 | 0.986 | 0.96 |
| stepanov-abstraction | 1.474 | 1.519 | 1.482 | 0.98 |
| functionobjects | 1.823 | 2.238 | 2.211 | 0.99 |
| olden-bh | 0.388 | 1.080 | 1.065 | 0.99 |
| olden-health | 0.079 | 0.146 | 0.126 | 0.86 |
| olden-treeadd | 0.053 | 0.100 | 0.095 | 0.95 |
| olden-tsp | 0.346 | 0.535 | 0.559 | 1.05 |
| olden-em3d | 0.941 | 1.022 | 0.999 | 0.98 |
| olden-voronoi | 0.083 | 0.217 | 0.203 | 0.93 |
| ptrdist-ks | 0.205 | 0.318 | 0.290 | 0.91 |
| ptrdist-yacr2 | 0.169 | 0.281 | 0.244 | 0.87 |
| ptrdist-ft | 0.254 | 0.408 | 0.390 | 0.96 |
| ptrdist-anagram | 0.267 | 0.442 | 0.416 | 0.94 |
| tramp3d | 0.086 | 0.449 | 0.156 | 0.35 |
| bullet | 1.352 | 1.909 | 1.801 | 0.94 |
| lame | 0.052 | 0.170 | 0.113 | 0.66 |
| sha | 0.009 | 0.067 | 0.042 | 0.63 |
| susan | 0.012 | 0.076 | 0.044 | 0.58 |
| mafft | 8.038 | 15.648 | 15.451 | 0.99 |
| nbench | 0.509 | 0.655 | 0.643 | 0.98 |
| ffmpeg-hevc-1t | 2.450 | 3.099 | 2.824 | 0.91 |
| ffmpeg-hevc-4t | 0.864 | 1.356 | 1.026 | 0.76 |
| ffmpeg-h264-1t | 1.490 | 2.392 | 1.934 | 0.81 |
| ffmpeg-h264-4t | 0.448 | 0.996 | 0.623 | 0.63 |
| geometric mean (startup excluded) | | 1.63 × native | 1.49 × native | 0.913 |

ReadyToRun takes 0.913 of the JIT's time, 1.49 times native against 1.63. It gains most where start-up and compilation dominate (tramp3d 0.35, susan 0.58, sha 0.63, lame 0.66, the startup probe 32 against 40 ms) and in ffmpeg (H.264 0.81 on one thread and 0.63 on four, HEVC 0.91 and 0.76). It loses where a function called once runs a long loop: precompiled code has no patchpoints for on-stack replacement and reads `initonly` addresses as fields, so the loop runs as crossgen2 compiled it (dhrystone 1.11, memcpy, callback and fannkuch 1.06, olden-tsp 1.05; heapsort, which the review listed, is level here at 0.98). The rule in `docs/user/deployment.md`: use it for a deployment whose host is known, when start-up and compilation or functions past the JIT's limits (`--diagnose-limits`) take the program's time; keep the IL image for a program that lives in one long loop, and time both. JP-10's partial image, which would precompile only the over-limit methods, is what would avoid choosing.

### Correctness

`tests/readytorun.sh`: a generated program with a function past the IL threshold matches native as a `.dll`, as a launcher and in system mode; with tiering off the IL image's JIT compiles that function with MinOpts and the ReadyToRun image's does not compile it at all; the option's misuses fail with one line. The benchmark set's ReadyToRun engine matches native on its 55 programs and ffmpeg's hashes.

## JP-09: MinOpts methods in the benchmark report

`scripts/benchmark.py` runs each JIT engine once more after its validation with `DOTNET_JitDisasmSummary=1`, lists the translated methods (of the image's `Program` classes) the summary shows compiled with MinOpts in `minopts.tsv`, and counts them per benchmark in `results.tsv` and the summary. The baseline has a `minopts` column, and a JIT row with more such methods than the baseline's fails the run. [tests/benchmarks.sh](../../tests/benchmarks.sh) checks the comparison on synthetic results: as many or fewer pass, one more fails. The summary first went to a file (`DOTNET_JitStdOutFile`) during the validation run, which made jpeg crash in 8 of 50 runs (segmentation faults, once `free(): invalid pointer`): the runtime's background tier-1 compilations write the file too, and with JP-02's setting they run from the start. With the summary on standard output, in a run of its own, jpeg ran 60 times without a failure.

## The baseline

`scripts/benchmark.py --update-baseline --output artifacts/bench/jp-m12` on this host, the whole set with the native, JIT and NativeAOT engines, at the tree of the JP-02 commit (`113adb3` with JP-02 to JP-05 and JP-09), wrote `tests/perf/baseline.tsv`; it replaces the baseline of `d2a2711` (2026-10-31), so it also carries the work merged since then (BE-33's local sharing, PF-14 to PF-19, the SIMD levels' scalar changes). Against that baseline, host-normalized, the JIT takes 0.835 of the time and NativeAOT 0.946, with no benchmark past the thresholds. The JIT runs at 1.70 times native and NativeAOT at 1.31 (geometric means over 57 benchmarks), from 2.11 and 1.45 in the `m8-final` run. The largest JIT gains are the programs JP-02 helps (olden-voronoi, float80, lame, olden-treeadd and olden-health, nbench, recursive); the largest JIT loss is heapsort, 4%. NativeAOT's largest losses are mutex (13%) and callback (11%), within the 25% threshold and not from this work, which changes NativeAOT code only through JP-03.

The baseline's new `minopts` column records the translated methods the JIT compiled with MinOpts: tramp3d 3 (`main` and two `advect` instantiations, past the IL threshold), mafft 1 (`constants`), ffmpeg's H.264 decodes 2 (`ff_h264_decode_mb_cabac`, `ff_h264_filter_mb_fast`), none elsewhere.

The run also showed that ptrdist-ft, which is checked against a musl-linked build, had not been validated since the vectorized engines were added: the musl reference was built only with them, and the failed validation did not keep the benchmark from being measured. Both are fixed (`5913686`); ptrdist-ft's outputs match the musl reference.

## Reproducing

```bash
scripts/benchmark.py --validate-only --engines native,jit --output artifacts/bench/jp02-images
# JP-02: the candidates as environment variables, rotated within each round
python3 artifacts/jit-patterns/jp02/sweep.py artifacts/bench/jp02-images 5 sweep-host.tsv 'default=' 'd0=DOTNET_TC_CallCountingDelayMs=0' 'd0t10=DOTNET_TC_CallCountingDelayMs=0 DOTNET_TC_CallCountThreshold=10'
SWEEP_PREFIX='taskset -c 8-11' python3 artifacts/jit-patterns/jp02/sweep.py artifacts/bench/jp02-images 5 sweep-4core.tsv 'default=' 'd0=DOTNET_TC_CallCountingDelayMs=0'
# JP-03 and JP-04: the same bitcode translated by a worktree of the base commit and by this compiler
python3 artifacts/jit-patterns/jp03/ab-set.py artifacts/bench/jp02-images set 5 "base=<base worktree>/bin/llvmnet" "new=$PWD/bin/llvmnet"
python3 scripts/ab-time.py 7 'base=dotnet base/interp.dll 640000 256' 'new=dotnet new/interp.dll 640000 256'
# JP-05: the limits report, and the JIT's own view of the same methods
bin/llvmnet --runtime=portable --diagnose-limits artifacts/ffmpeg-browser-portable/decode.bc -o decode.dll
DOTNET_JitDisasmSummary=1 DOTNET_JitStdOutFile=summary.txt dotnet decode.dll artifacts/bench/clips/h264-1080p.mp4 100000 video 1 frame hash
tests/run.sh semantics constant-globals benchmarks
```

Runs were taken in `systemd-run --user --scope -p MemoryMax=...` scopes.
