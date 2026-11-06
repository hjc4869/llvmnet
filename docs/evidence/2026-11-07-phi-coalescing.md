# Evidence: PF-22 and PF-23, phi copies and field addresses in large functions (2026-11-07)

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

## PF-23: constant-offset GEPs computed at their uses

### The cause

LLVM hoists a structure's field addresses into a loop head for the blocks below it: `S_regmatch`'s block `%192` computes 19 of them (`getelementptr inbounds nuw i8, ptr %212, i64 32`, ...) for its switch's cases. JP-05 computes a value where it is used only when that is its one use, in its block, so each had a local, and NativeAOT's code computed and stored all 19 on every pass of the dispatch loop (`lea rbx,[r9+0x20]`, `mov [rbp-0x338],rbx`), where native code folds each into its users' addressing modes.

### The change

`ChooseUseSiteValues` also computes at each of its uses, in any block, a GEP whose indices are all constant integers and whose base is an argument or a value with a local (neither computed where it is used nor itself such a GEP). ShareFunctionLocals already counts a value computed where it is used as a use of its operands there, so the base is live where the GEP was used, and an edge that copies such a GEP into a phi computes it there. `Gep` adds a GEP's constant indices and fields into one offset, added once with a 32-bit constant where it fits, instead of an `ldc.i8` multiplication and addition per index, which keeps a GEP computed at each of its uses small: `getelementptr i8, ptr %p, i64 32` was 22 bytes of IL besides its base's load (`ldc.i8 conv.i8 ldc.i8 mul conv.i add`) and is 4 (`ldc.i4.s conv.i add`).

### Code

| `S_regmatch` | PF-22 | PF-23 |
| --- | ---: | ---: |
| IL bytes | 64,968 | 55,887 |
| IL instructions | 22,180 | 22,110 |
| locals | 143 | 105 |
| local references | 7,621 | 7,383 |
| NativeAOT machine code, bytes (native 25,027) | 42,717 | 41,294 |
| machine instructions | 9,353 | 9,056 |
| moves between a register and a stack slot | 2,287 | 2,057 |
| constant-offset addresses stored to a stack slot | 49 | 11 |

Block `%192` now goes from `%181`'s loads straight to the switch; none of its 19 addresses is computed or stored there. Folding the offsets is most of the IL saved: perlbench's IL goes from 5,427,961 to 4,358,022 bytes, and to 4,317,244 with the folding alone; in `S_regmatch` computing the GEPs at their uses adds 2,002 bytes and 1,547 IL instructions to the folding alone, and removes 38 locals and 238 local references. Methods past a MinOpts threshold stay at 11: `my_re_op_compile`, past the instruction limit before (20,427 instructions), falls below it with the folding alone (19,305) and is back over it (20,170). The translated machine code goes from 3,188,631 to 3,105,716 bytes, and `S_find_byclass` has 978 register and stack-slot moves (1,539 after PF-22, 1,449 before). FFmpeg's decode harness goes from 15,981,859 to 12,711,550 bytes of IL, with 16 methods past a MinOpts threshold as before, 11 of them past the IL, block or local thresholds instead of 14. In the benchmark set the JIT no longer compiles tramp3d's `main` with MinOpts.

### Speed

perlbench as for PF-22. PF-22's build against this one side by side, three rounds, on the loaded host; medians, elapsed and user seconds:

| workload | PF-22 | PF-23 | PF-23/PF-22 | user PF-22 | user PF-23 | PF-23/PF-22 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| checkspam | 229.6 | 165.3 | 0.72 | 224.1 | 163.7 | 0.73 |
| diffmail | 79.2 | 72.8 | 0.92 | 78.8 | 72.1 | 0.92 |
| splitmail | 205.2 | 187.1 | 0.91 | 196.8 | 180.8 | 0.92 |
| all three | 514.0 | 425.2 | 0.83 | 499.7 | 416.7 | 0.83 |

The build before PF-22 against this one, three rounds, later and on a quieter host (load average 4 to 12; a core gate run started during the third round): checkspam 241.3 s to 104.5 (0.43), diffmail 92.8 to 60.7 (0.65), splitmail 103.8 to 96.6 (0.93; the rounds 0.79, 0.95 and 1.25), all three 438.0 to 261.9 (0.60). The 2026-11-06 run took 355 s llvmnet and 150 s native for the three. In the sampler's profiles of checkspam, PF-22's build against this one side by side, `S_regmatch` takes 42.1 s instead of 62.8 and the whole run 155.2 instead of 175.1. `Perl_leave_scope`, which is smaller (1,434 machine instructions instead of 1,711, 355 slot moves instead of 532), took 9.8 s instead of 7.7; a third of its samples are on the jump-table dispatch of its switch, the same code in both builds.

### The benchmark set

PF-23 changes the IL of nearly every function, through the folded offsets and the GEPs computed at their uses. NativeAOT, as for PF-22 (five rounds, user seconds), PF-23 against PF-22: heapsort 0.98, lpbench 0.98, bullet 0.89, lame 1.02, linpack 1.02, mafft 1.00, nbench 0.99, reed-solomon 0.99, stepanov-abstraction 1.03, tramp3d 0.98, olden-em3d 1.01, ptrdist-anagram 0.96, ptrdist-yacr2 1.00, richards 1.04, mutex 0.97, atomics 0.99, printf 1.02, malloc 1.00, a geometric mean of 0.992 (0.979 against the build before PF-22, without olden-em3d). Seven rounds of others: fftbench 1.08 (1.05 under the JIT), olden-bh 0.99, almabench 1.00, hash 0.97. More rounds of three: richards 1.08 as NativeAOT (11 rounds) and 0.97 under the JIT (7), heapsort 0.96 and 1.03, lpbench 1.03 and 1.02. `scripts/benchmark.py --group micro --group suite --repeats 3` with this compiler passes against `tests/perf/baseline.tsv`: host-normalized geometric means 0.985 under the JIT and 0.981 as NativeAOT over 53 benchmarks, no benchmark past its threshold, mafft's `constants` the one method compiled with MinOpts. FFmpeg's decodes with one thread as NativeAOT: 1.00 (H.264) and 0.98 (HEVC) of PF-22's time, frame hashes matching native.

Two programs are slower, and their hot loops are smaller. fftbench's `polynomial<double>::fft` (149 values) loads both halves of a complex value as `[r9]` and `[r9+0x8]` instead of computing `r9+8` into a register and spilling it, yet takes 10% longer in the sampler's profile, the extra samples on those cache-missing loads, in both builds' layouts and under the JIT. richards' `schedule` (41 values) folds its field addresses the same way and is 10% slower as NativeAOT, 3% faster under the JIT. Computing GEPs at their uses only in functions of more than 512 values, the ones that share locals, measured against PF-22: fftbench 1.00 instead of 1.16, richards 1.02 instead of 1.04, heapsort 1.01 instead of 1.03, but ptrdist-anagram 1.03 instead of 0.89, bullet 0.95 instead of 0.89, nbench 1.00 instead of 0.98, tramp3d 1.00 instead of 0.96, and perlbench's splitmail 1.09 of the time with every function (checkspam and diffmail the same). Every function keeps it; the two slower loops are open.

### Correctness

`tests/ir/constant-geps.ll` computes field addresses of an argument in a loop head, used in the cases below and copied into a phi on their edges, with struct and array indices added into one offset, a GEP of such a GEP, a negative `i32` index and offsets past 32 bits, against native in both modes under the JIT. The core gate passed 105 of 105 tests. The 32 IR tests also pass as portable NativeAOT; 30 more generated dispatch loops (`tests/c/phi-copies.py`, seeds 100 to 129) at three levels in both modes matched native, as did every program of the benchmark set under the JIT and NativeAOT, perlbench's refrate outputs and FFmpeg's frame hashes.
