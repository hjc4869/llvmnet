# Evidence: SPECrate 2017 integer, llvmnet Vector128 against native SSE2 (2026-11-06)

Host: AMD Ryzen AI MAX+ 395 (Zen 5), Linux 6.12.113, .NET 10.0.14 (ILCompiler 10.0.14), clang, clang++ and flang 22.1.8; llvmnet links the same `libLLVM.so.22.1`. Branch `x264-perf`. A development measurement with the licensed kit (`artifacts/spec2017-harness`), not a reportable SPEC result: refrate, one copy, three iterations, `--noreportable`.

## SV-31: early-exit loops

llvmnet's first build of the suite at `-O3` with the vectorizers on failed: 502.gcc_r's link stopped at `llvm.experimental.cttz.elts.i64.v2i1` in `init_regs`. LLVM 22's loop vectorizer vectorizes early-exit loops over memory it can read whole, such as `for (i = 0; i < 300; i++) if (a[i] == v) return i;` on a global array, and leaves the vector loop through `cttz.elts`, the index of the first true lane of the exit mask. The typed lowering has no translation, LLVM's scalarizer leaves the call as it is, and so any program with such a loop failed to link at `-O2` and above, the levels that vectorize since SV-05; the SPEC runs before SV-05 used `-fno-vectorize`. `tests/vector-sweep.sh` lists the builds the compiler rejects without failing, and its fixtures had no early exit.

What changed: the driver expands `cttz.elts` before the lowering (`llvmnet_expand_cttz_elts`, [native/vectors.cpp](../../native/vectors.cpp)) into the `umin` reduction of the lane indices the mask selects, the lane count in the other lanes, as SelectionDAG expands it generically; the indices are 8-bit lanes below 256 lanes, so a 16-lane byte search reduces one `Vector128<byte>`.

**Correctness.** [tests/ir/vector-lowering.ll](../../tests/ir/vector-lowering.ll)'s `firsts` takes the first lane of 16- and 2-lane masks, with and without a true lane; [tests/c/vector-loops.c](../../tests/c/vector-loops.c) adds a byte search over 300 bytes and a flag search over 64 `int32_t`, found and not found, which clang-22 at `-O3` vectorizes into four `cttz.elts`; `tests/vector-sweep.sh` builds it in both modes at `-O3`, `-ffast-math` and `x86-64-v3` (26 builds, all matching native, none rejected) and as NativeAOT. The vector tests, `semantics` and `ir` pass.

## SV-32: masks of more than 64 lanes in scalarized functions

The first timed llvmnet run failed 548.exchange2_r at once: a `NullReferenceException` in `_QQmain`, also under the JIT, also with `adfdc44`'s compiler (once SV-31 let it link), and not with the hidden scalar lowering. flang vectorizes the main program's 80-cell puzzle checks into `<80 x i32>` loads, `<80 x i1>` comparisons, `bitcast <80 x i1>` to `i80` and `ctpop.i80`. The typed lowering holds a mask as bits in one `ulong`, so `VectorEmitter.Legalize` scalarizes a function with a mask of more than 64 lanes; LLVM's scalarizer then rebuilds the `<80 x i1>` for the bitcast as an `insertelement` chain, but `TypeSystem.Vector` still gave that type a typed mask shape, so the chain was built as a typed mask, which has no lanes past 64, and the program read wrong cells.

What changed: `TypeSystem.Vector` gives masks of more than 64 lanes no shape, so in a scalarized function they are aggregates as without the lowering (`Legalize` reports them as before, "more than 64 i1 lanes").

**Correctness.** [tests/ir/vector-wide-masks.ll](../../tests/ir/vector-wide-masks.ll) compares 80 lanes, counts the mask's bits and reads its top lanes and lane 71; the previous compiler stopped it with an `AccessViolationException`, now it passes in both modes (`ir.sh`). exchange2 runs its refrate workload to SPEC's expected `s.txt`. `ir`, the vector tests and `fortran` pass.

## SV-33: vectors of function addresses as NativeAOT

502.gcc_r compiled its first input into "internal error: in ?, at gimplify.c:6493" as NativeAOT at every instruction set and optimization preference, while every JIT build (also without AVX2, AVX or hardware intrinsics, with tiering off) and the scalar lowering as NativeAOT wrote the same assembly. Scalarizing one typed function at a time (a temporary switch in `Legalize`, eight relinks) found `gimplify_expr`, whose one vector operation is the SLP vectorizer's comparison of a function pointer with eight functions (`icmp eq <8 x ptr>` against a constant vector of `@is_gimple_val` and seven others). To ILC a function's address (`ldftn`) is a relocatable constant, and RyuJIT folds `Vector128.Create` of constants into a vector literal: the NativeAOT image's literal held the handles' compile-time values (`0x422a20`, ...) with no relocation, not the functions' addresses (`0x1590b0`, ...), so no pointer ever compared equal. The JIT folds the real addresses. An eight-line C program reproduces it.

What changed: a lane that is a function's address, or a constant expression that may contain one, goes through `Vectors.OpaqueAddress` (or `Opaque` for integer lanes), a call ILC does not fold, wherever the lowering builds a vector from scalars (`VectorEmitter.LoadLane`: constant vectors, `insertelement`, splats).

**Correctness.** [tests/c/vector-loops.c](../../tests/c/vector-loops.c)'s `known` compares function pointers with eight functions, which clang-22 at `-O3` vectorizes so; as NativeAOT the previous compiler printed another checksum than native, now `tests/vector-sweep.sh`'s NativeAOT build matches. gcc's first refrate input now compiles to the JIT builds' assembly. The vector tests, `x86-intrinsics`, `indirect-calls`, `semantics` and `ir` pass.

## Results

Native: clang, clang++ and flang 22.1.8 at `-O3`, for the x86-64 baseline (SSE2), linked against the host's glibc 2.43 and libstdc++ 16.2, with only the per-benchmark portability flags (`artifacts/x264-perf/spec/native-clang22.cfg`). llvmnet: `config/spec/llvmnet.cfg` at `-O3 -fvectorize -fslp-vectorize` (the typed lowering to `Vector128`), portable mode (musl 1.2.5, libc++ 22.1.8), NativeAOT, which compiles images with vector code for x86-64-v2 (SV-05), with the toolchain packaged at `220c0f4`. One benchmark at a time on an otherwise idle host, not pinned to a core; seconds are SPEC's median of three.

| Benchmark | native (s) | llvmnet (s) | llvmnet / native |
| --- | ---: | ---: | ---: |
| 500.perlbench_r | 150 | 355 | 2.37 |
| 502.gcc_r | 125 | 164 | 1.31 |
| 505.mcf_r | 252 | 268 | 1.06 |
| 520.omnetpp_r | 236 | 388 | 1.64 |
| 523.xalancbmk_r | 118 | 114 | 0.97 |
| 525.x264_r | 83.8 | 99.1 | 1.18 |
| 531.deepsjeng_r | 193 | 247 | 1.28 |
| 541.leela_r | 232 | 281 | 1.21 |
| 548.exchange2_r | 109 | 155 | 1.42 |
| 557.xz_r | 228 | 254 | 1.11 |
| geometric mean | | | **1.31** |

Every run validated. The estimated SPECrate2017_int_base is 6.92 against native's 9.10. The run before SV-32 and SV-33 (toolchain at `f78dd0d`, gcc and exchange2 failing) took the same time on the other eight to within 1%, except omnetpp (371 s, whose runs spread from 378 to 399 s in this one) and x264 (96.2 s; 95.8 to 99.3 s here). x264's three refrate workloads take 1.18 times native; its first alone took 1.20 in the [x264 evaluation](2026-11-06-x264.md).

## Profiles of the two largest gaps

`perf` cannot run on the host, so the profiles come from the x264 evaluation's sampler (`artifacts/x264-perf/tools/libsampler.so`, a timer signal that records the main thread's program counter every 100 µs), over every refrate workload, the llvmnet side from builds relinked from the SPEC run's objects with `--aot-debug` for symbols. Their outputs matched native's. The profiles took 161 s native and 365 s llvmnet for perlbench (150 and 355 timed), 232 s and 389 s for omnetpp (236 and 388).

Native omnetpp is sensitive to which copy of its executable runs: copied with its run directory (to tmpfs or to the same ext4 disk), it took 284 to 310 s in five runs, pinned or not and with or without the sampler, against 225 to 249 s for SPEC's own file in six (the three timed runs, one more through `runcpu`, one through `specinvoke` and one started directly), while llvmnet's image took 388 to 390 s either way. The native omnetpp profile is therefore from SPEC's file in its run directory; the sampler gained a `PROF_ONLY` variable to sample only the benchmark under `specinvoke`.

**500.perlbench_r (2.37).** Of the 204 s gap over the three workloads, `S_regmatch`, Perl's regular-expression matcher, is 149 s (194 s against native's 45 s, 73%); then `Perl_runops_standard` (10.4 s against 1.9), `S_find_byclass` (18.4 against 11.0) and `Perl_regexec_flags` (8.1 against 4.3). `S_regmatch` is one function of 5,653 LLVM instructions (106 KB of IL, 369 locals after BE-33's sharing; 84 KB of machine code against native's 25 KB), a state machine whose dispatch loop carries 23 to 29 values through a chain of phi blocks (`%104`, `%133`, `%157`, `%192`, each forwarding the values of the block before it or of a back edge). 61% of its samples are moves between a register and a stack slot and 7% address computations (`lea r, [r + offset]`); the rest, about 60 s, is close to native's whole function. Two causes, both generic:

1. **Phi copies.** BE-33 shares a local between values whose live ranges, spans in emission order, do not overlap. A loop phi's span runs from its block to the edge that stores it, which for a back edge is near the end of the function, so the chained phis and the values they copy all overlap and none share. RyuJIT keeps few of those locals in registers in a function of this size, and every edge copies its values from one stack slot to another (`mov ecx,[rbp-0x490]`, `mov [rbp-0x538],ecx`). Runs of 20 or more such moves in a row hold 101 s of the program's 365, half the gap; the longest, 134 moves on one edge, 39 s. Native code gives a phi and the values it copies one register or stack slot (LLVM's register coalescer), so the same edges cost nothing. Giving a phi its incoming values' local where they do not interfere (SSA interference instead of overlapping spans) would remove the copies (PF-22).
2. **Addresses computed where LLVM put them.** `%192` computes 19 field addresses of the matcher's state (`getelementptr i8, ptr %212, i64 32`, ...), which LLVM hoisted there for the switch cases below; llvmnet stores each in a local whenever the loop passes the block (`lea rcx,[rdi+0x20]`, `mov [rbp-0x510],rcx`), where native code folds each into its users' addressing modes. JP-05 computes a value at its use only when that is its one use, in its block; a constant-offset GEP of a value that has a local could be computed at each of its uses (PF-23).

`Perl_runops_standard`, the loop that calls each op's function, loads `PL_op`'s address from a static field, then `PL_op`, then the function, and tests it for NativeAOT's fat-pointer bit before `call r15`; native code keeps the address in a register and calls `[rax+0x10]`.

**520.omnetpp_r (1.64).** Native spends 48.5 s of its 232 s (21%) outside its executable, in glibc and libstdc++. llvmnet spends 106 s in the same services linked into its image: 82 s in musl's allocator (mallocng's `get_meta` 38 s, `alloc_slot` 19 s, `malloc` 14 s, `free` and `nontrivial_free` 10 s), 14 s in libc++abi's `__dynamic_cast` and its class-hierarchy search, and 10 s in musl's `memcmp`, `strcmp` and `strlen`. That is 57 s of the 158 s gap. omnetpp's own code is the other 101 s, 1.55 times native (284 s against 183), spread over many small methods: `cGate::deliver` 1.79 times, `cIndexedFileOutputVectorManager::record` 2.35, `cSimpleModule::sendDelayed` 2.83.

1. **The variadic call area.** A function that makes a variadic call anywhere allocates the area for it on entry with `localloc` (`VarArgEmitter.Allocate`), and RyuJIT gives a method with `localloc` a frame pointer, a stack probe and NativeAOT's stack cookie, checked on return. omnetpp's methods throw `cRuntimeError(const cObject*, const char* fmt, ...)` on their error paths, so 25 of the 60 hottest, with 31% of the program's samples, pay for it on every call: `cGate::deliver` is 725 bytes against native's 264, saves six registers instead of three and reserves 0x160 bytes for a throw it does not make. A frame slot of the call's size, as static allocas have, would leave such methods a plain frame (PF-24).
2. **Virtual calls** test the target for NativeAOT's fat function pointer bit before `call r12`, two instructions and a branch per call, and a method that reads a global checks that the static base is initialized on entry.
3. **musl's allocator.** mallocng checks a slot's metadata on every `free`: `get_meta`'s samples are on the slot header and on `meta->mem == base`, a load through the group header's pointer into a separate metadata area, in omnetpp's stream of short-lived messages. LC-07 chose mallocng over routing `malloc` to `NativeMemory` for deterministic behavior and `malloc_usable_size` parity; whether to measure or adopt another allocator for the portable C library is the owner's decision (PF-25).
