# Evidence: PF-22, phi copies in functions that share locals (2026-11-07)

Host: AMD Ryzen AI MAX+ 395 (Zen 5, 16 cores, 32 threads), 124 GB, Linux 6.12.115, .NET 10.0.14 (ILCompiler 10.0.14), clang 22.1.8. Branch `claude/phi-coalescing-4bf36c` from `x264-perf` at `22a0f55`. Found by the SPECrate 2017 integer profile ([evidence](2026-11-06-spec-intrate.md#profiles-of-the-two-largest-gaps)).

## PF-22: phi copies

### The cause

BE-33 ([evidence](2026-11-04-be33-local-sharing.md)) gives values one local when their live ranges, spans in emission order, do not meet, and a phi's span starts at the edges that store it. A dispatch loop's phis, the phis that forward them and the values they copy all overlap at those edges, so none shared, and each edge copied its values from one stack slot to another. 500.perlbench_r's `S_regmatch` passes 23 to 29 values through `%104`, `%133`, `%157` and `%192`, each block's phis taking the values of the block before it or of a back edge: its machine code had 131 runs of 20 or more moves between a register and a stack slot, the longest 134, and 42.8% of its samples on checkspam were in them.

### The change

`FunctionEmitter.ShareFunctionLocals` coalesces phis with the values they copy before its linear scan, as LLVM's register coalescer joins a copy's live intervals:

- Each phi and each instruction a phi copies gets live segments from the liveness bit sets the pass already computes: in each block it is defined in or live into, from its definition (a phi's at the block's start) or the block's start, to its last use there or the block's end.
- A phi joins the class of a value it copies when no segment of a member of one class meets a segment of a member of the other (SSA interference). An edge loads every value it copies before it stores any phi, so a value whose last use is the edge does not conflict with the phi it is copied into; one that stays live past the edge, or a phi still live where the value is defined, does. The phis of one block all start at its start and never join, and an instruction reads its operands where its result is written, so BE-33's rule that a result never takes its operands' local still holds: an induction variable's next value keeps a local of its own.
- Copies between phis join first. A value computed from a phi of a forwarding chain and copied into another phi of it (`%4936 = add nsw i32 %208, 1`, copied into `%125`), joined first, kept the chain apart: `S_regmatch` had 422 copies blocked by another member of a class in phi order, 294 with copies between phis first.
- A class takes one local in the linear scan, over the span of its members' ranges, so it shares with other values only where BE-33 would have let all of its members share.
- `Edge` emits no load and store for a phi whose local is the copied value's. The edge's other copies keep their order, every load before any store, so swaps and cycles stay parallel assignments.

Functions of 512 values or fewer keep a local per value; functions with `setjmp` and those of more than 32,768 values keep their schemes.

### Code

`S_regmatch` (5,653 LLVM instructions) has 5,290 phi inputs: 278 constants and arguments, 4,617 that now share the phi's local, 101 that interfere with their phi (mostly an induction variable's next value) and 294 that interfere with another member of the phi's class. The dispatch loop's chains are one class each; what is left of their copies is values computed from a member (`%5083 = add nsw i32 %5058, -1`).

| `S_regmatch` | before | after |
| --- | ---: | ---: |
| IL bytes | 106,056 | 64,968 |
| IL instructions | 42,724 | 22,180 |
| locals | 369 | 143 |
| local references | 16,845 | 7,621 |
| NativeAOT machine code, bytes (native 25,027) | 84,435 | 42,717 |
| machine instructions | 16,200 | 9,353 |
| moves between a register and a stack slot | 7,765 | 2,287 |
| runs of 20 or more such moves (longest run) | 131 (134) | 0 (7) |

perlbench's IL goes from 5,624,525 to 5,427,961 bytes (methods past a MinOpts threshold 13 to 11) and its translated machine code from 3,323,478 to 3,188,631 bytes. `Perl_sv_vcatpvfn_flags` loses its 11 runs of 20 or more moves (2,276 moves to 1,458). Two hot functions have more moves in fewer instructions, because a class is one RyuJIT allocation: `S_find_byclass` 1,449 moves to 1,539 (9,344 instructions to 9,091) and `Perl_yylex` 799 to 1,015 (18,067 to 17,801). FFmpeg's decode harness goes from 16,145,622 to 15,981,859 bytes of IL, methods past a MinOpts threshold 17 to 16.

### Speed

The SPEC run's objects relinked with each compiler (`build_base_llvmnet_portable_O3_v1_c14_t1.0000`, the link line of its `make.out`), each refrate workload run from a fresh copy of the run directory and executable, pinned to one core. The host was shared with other sessions' gates and builds (load average 20 to 60 on 32 threads), which made every time about twice the 2026-11-06 run's, so before and after ran at the same time on two cores, swapping cores each round, three rounds; medians, elapsed and user seconds:

| workload | before | after | after/before | user before | user after | after/before |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| checkspam | 423.5 | 296.2 | 0.70 | 351.7 | 250.1 | 0.71 |
| diffmail | 146.2 | 103.7 | 0.71 | 134.2 | 96.9 | 0.72 |
| splitmail | 239.2 | 209.7 | 0.88 | 220.8 | 190.4 | 0.86 |
| all three | 809.0 | 609.6 | 0.75 | 706.7 | 537.4 | 0.76 |

Every output matched SPEC's expected one. In the sampler's profiles (`--aot-debug` builds, the same load), `S_regmatch` went from 161.3 to 59.8 s on checkspam and from 65.1 to 15.7 s on diffmail, against 0.85 and 0.65 for `Perl_runops_standard`, which PF-22 does not change (the load's share of the difference); `S_find_byclass`, which has more moves, went from 20.5 to 17.5 s on checkspam, also 0.85.

### The benchmark set

PF-22 changes only functions of more than 512 values. In heapsort and lpbench, BE-33's small-loop cases, the IL of every method but musl's `printf_core` and `alloc_slot` is identical byte for byte. In the rest of the micro and suite groups it changes the code of bullet, jpeg, lame, linpack, mafft, nbench, reed-solomon, stepanov-abstraction, susan and tramp3d, and of musl's and libc++'s large functions in the others. Those ten, BE-33's cases and the others BE-33 measured, as NativeAOT executables built by each compiler from the same sources (`scripts/benchmark.py --validate-only`, outputs matching native), five rounds in rotating order pinned to one core, a fresh copy of the executable each run; user seconds, medians:

| benchmark | before | after/before |
| --- | ---: | ---: |
| heapsort | 2.918 | 0.99 |
| lpbench | 2.608 | 1.03 |
| bullet | 3.452 | 1.03 |
| lame | 0.119 | 0.98 |
| linpack | 1.603 | 0.98 |
| mafft | 19.668 | 0.93 |
| nbench | 1.019 | 0.97 |
| reed-solomon | 3.168 | 0.99 |
| stepanov-abstraction | 1.604 | 0.97 |
| tramp3d | 0.174 | 1.00 |
| olden-em3d | 1.424 | 1.25 |
| ptrdist-anagram | 0.550 | 0.99 |
| ptrdist-yacr2 | 0.343 | 0.97 |
| richards | 0.569 | 1.00 |
| mutex | 0.235 | 1.01 |
| atomics | 0.225 | 1.00 |
| printf | 3.228 | 0.96 |
| malloc | 1.297 | 0.99 |

The geometric mean is 1.000, and 0.987 without olden-em3d (jpeg and susan, which run 4 and 24 ms, are left out). olden-em3d's own code is unchanged: its hot loop is PF-20's, whose time depends on where it lands, and `make_neighbors` is the same machine code in both images, starting 32 bytes into a 64-byte line before and on a line boundary after; built with `--aot-debug`, which places it elsewhere, both take 0.94 s. A second run of heapsort and lpbench (11 rounds as NativeAOT, 7 under the JIT) has them at 1.00 and 0.98 as NativeAOT and 1.00 and 1.00 under the JIT. FFmpeg's decode harness, its linked bitcode translated by each compiler, decodes with one thread as NativeAOT in 0.99 (H.264) and 1.01 (HEVC) of its time before, frame hashes matching native (five rounds).

### Correctness

`tests/phi-copies.sh` (core) generates dispatch loops of about 1,450 LLVM values and 230 phis (`tests/c/phi-copies.py`): 40 variables of five types, whose 44 cases rotate them in cycles of two to seven (swaps among them), forward them through branches and small inner loops, share switch targets, and keep values from before the last pass live past the loop. Three programs must print what their native builds print in both modes under the JIT, two as NativeAOT, and `run` must have fewer than 110 locals (72 to 77 with coalescing, 174 to 182 without). Before the test, 70 such programs (seeds 10 to 79) at `-O1`, `-O2` and `-O3` in both modes, 420 builds, matched native. The core gate passed 104 of 105 tests; `x86-intrinsics` failed when `grep -q` closed a pipe Clang was still writing its macros to (`error: unable to open output file '-': 'Broken pipe'`) and passed when run again.
