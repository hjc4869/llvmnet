# Evidence: the typed vector lowering, ADR-16 Level 1 core (SV-02), 2026-11-03

LLVM vector IR now runs as `Vector128<T>` code instead of scalar code ([12-simd.md](../plan/12-simd.md#level-1-general-vector-lowering), [architecture.md](../dev/architecture.md#values-and-control-flow)). Every result below comes from a command in this repository on the build host (AMD Ryzen AI MAX+ 395, AVX-512, .NET 10.0.14, LLVM 22.1.8) and the `xp` hosts (`fluorite`, Windows x64; `mainmac`, macOS ARM64), from the `sv-level1` branch.

## What was built

- **Types** (`TypeSystem.Vector`, `VectorShape`): a vector of `i8`, `i16`, `i32`, `i64`, pointer, `float`, `double` or `i1` lanes is one `Vector128<T>` up to 128 bits (upper lanes of a shorter vector undefined) and a sequential struct of `Vector128<T>` fields beyond; integer lanes are signed and reinterpreted where an operation needs another view; an `i1` vector is a mask of all-ones or zero lanes, 128 bits over its lane count rounded up to a power of two (8 to 64 bits per lane).
- **Instructions** (`VectorEmitter.cs`): arithmetic, logic, shifts by a splat (and by a vector, lane by lane), integer division and remainder lane by lane on the defined lanes only, comparisons (all 16 `fcmp` predicates composed from the IEEE comparisons), `select` through `ConditionalSelect` with the mask at the selected lanes' width (a comparison of another width is computed again at the select instead of converted), casts through `Widen`/`Narrow` trees and the `ConvertTo*` methods (64-bit integers to `float` lane by lane, which rounds once), bitcasts, `extractelement`/`insertelement` with constant and variable indices, `shufflevector` per result fragment (identity, constant `Shuffle`, `Create` of halves, `ConditionalSelect` of shuffles), loads and stores of exactly the store size (`Vectors.Load`/`Store` below 16 bytes, `i1` vectors as bits), constants as `Vector128.Create`, every `llvm.vector.reduce.*` (expand-reductions skips optnone functions) and the core generic intrinsics (min, max, `abs`, saturating add and subtract, `fabs`, `sqrt`, `fma`, `fmuladd`, `minnum`/`maxnum`/`minimum`/`maximum`, `copysign`, rounding).
- **Pipeline**: `opt` no longer runs the scalarizer; `VectorEmitter.Legalize` scalarizes, through `llvmnet_scalarize_functions` (`native/vectors.cpp`, LLVM's `ScalarizerPass` with load-store splitting per function), each function the lowering does not translate and each function whose vector code moves more lanes than it computes on (below). `llvmnet -v` names them. The hidden `--vector-lowering=typed|scalar` (default `auto`) keeps every translatable function typed or scalarizes everything as before.
- **Boundaries**: parameters are typed; a vector result of at most 16 bytes returns as its explicit-layout bytes (below).
- **Tests**: `tests/vector-lowering.sh` with `tests/c/vector-extensions.c` (Clang's vector extensions over 39 vector types, from `<3 x i8>` to `<64 x i8>` and `<16 x float>`, including bool vectors, odd lane counts and `__int128` lanes), `tests/ir/vector-lowering.ll` (what C does not reach: every `fcmp` predicate with NaN, pointer vectors, mask shuffles and variable lane indices, masks in memory, one-lane vectors, optnone reductions, intrinsics without a lowering), `tests/vector-sweep.sh` now requiring vector code from `vector-loops.c`, the `xp` fixtures `vector-extensions-O0` and `-O2` and NativeAOT for `vector-loops`, and `LlvmNet.Checks vector-calls`, which counts an image's calls into `Vector128` and `LlvmNet.Runtime.Vectors`.

## Correctness

| Check | Result |
| --- | --- |
| `vector-extensions.c` at `-O0`, `-O1`, `-O2`, `-O3`, portable and system, default and `--vector-lowering=typed` (16 builds) | output identical to native clang; 691 to 1,153 `Vector128` calls per image; under `typed` only `fallback` (popcount, `__int128` lanes) is scalarized |
| the same with `DOTNET_EnableHWIntrinsic=0` (portable and system `-O2`) and `DOTNET_EnableAVX=0` | identical |
| the same as NativeAOT: portable `-O2`, system `-O0`, portable `-O2` with `--aot-instruction-set x86-64-v3` | identical |
| `tests/ir/vector-lowering.ll`: native, portable, system, NativeAOT, hardware intrinsics off, default and `--vector-lowering=typed` | all return 0 |
| `tests/vector-sweep.sh` (vector-loops and five other fixtures, `-O3 -fvectorize`, fast-math and `-march=x86-64-v3`) | 20 builds identical to native; vector-loops as 148 (portable), 132 (system), 176 and 160 (fast-math) and 741 (AVX2, with `<8 x i32>`, `<16 x i32>`, masked stores and reductions) `Vector128` calls |
| `xp` bundle on Windows x64 (`fluorite`), JIT and NativeAOT | 69 passed, 0 failed, 4 skipped (the `gles` and `vulkan` extensions without host libraries, as before) |
| `xp` bundle on macOS ARM64 (`mainmac`), JIT and NativeAOT | 69 passed, 0 failed, 4 skipped |
| Linux ARM64: the vector fixtures, `smoke`, `core`, `math`, `cpp-streams`, `fortran-core`, `fortran-complex-io` in the `mcr.microsoft.com/dotnet/runtime:10.0` arm64 container under qemu (JIT) | 9 passed |
| the pull-request gate, `tests/run.sh --tag core --tag toolchain --exclude-tag slow`, in a 48 GB scope | 108 passed, 0 failed (332 s) |

The Linux ARM64 NativeAOT cross-publish did not run: `llvmnet --nativeaot --runtime-id linux-arm64` stops at its linker probe ("zlib's development files are missing") because the build host has no arm64 `zlib1g-dev`; NativeAOT on ARM64 ran on macOS.

## Generated code

`DOTNET_TieredCompilation=0 DOTNET_JitDisasm=saxpy` for `y[i] = a * x[i] + y[i]` compiled with `-O3 -fvectorize` (LLVM chose four lanes, interleaved twice) shows the loop as native SSE code would be, with VEX encoding on this host:

```
vmovups  xmm2, xmmword ptr [r9]
vmovups  xmm3, xmmword ptr [r10]
...
vmulps   xmm2, xmm1, xmm2
vaddps   xmm2, xmm2, xmm4
vmulps   xmm3, xmm1, xmm3
vaddps   xmm3, xmm3, xmm5
vmovups  xmmword ptr [r8], xmm2
vmovups  xmmword ptr [r9], xmm3
```

An integer kernel with a select and a widening sum (`v = in[i] * 3 + 9; out[i] = v < 0 ? -v : v; total += out[i]`, vectorized as `<2 x i32>`) becomes `vmovq` loads (the partial-load helper inlines away), `vpmulld`, `vpcmpgtd` and `vpblendvb` for the select, `vpmovsxdq` for the widening and `vpaddq`, with `vmovq` stores.

## The scalar benchmark set

The benchmark set of ADR-08 is compiled without the vectorizers, so only vector IR from other sources can change it. Each of its 54 non-ffmpeg programs was built with the default lowering and with `--vector-lowering=scalar` into directories of the same name, and the images compared:

- 49 images are byte-identical, and so is the ffmpeg decode harness (`artifacts/ffmpeg-browser-portable/decode.bc`, no vector IR after lowering).
- 5 differ: `fftbench`, `hash2-cxx` and `tramp3d` through libc++'s `locale.o` (a `<32 x i8>` comparison in `num_get`'s atom lookup, 78 `Vector128` calls), `olden-health`, and `bullet`.

The five, timed with `scripts/ab-time.py 9` (alternating, seconds, median and minimum):

| Benchmark | typed JIT | scalar JIT | typed NativeAOT | scalar NativeAOT |
| --- | ---: | ---: | ---: | ---: |
| fftbench | 0.991 (0.943) | 1.002 (0.938) | 0.859 (0.792) | 0.834 (0.788) |
| hash2-cxx | 1.124 (1.070) | 1.114 (1.075) | 0.928 (0.891) | 0.927 (0.901) |
| olden-health | 0.197 (0.192) | 0.189 (0.182) | 0.097 (0.086) | 0.101 (0.085) |
| tramp3d | 0.613 (0.608) | 0.612 (0.607) | 0.107 (0.102) | 0.109 (0.103) |
| bullet | 2.592 (2.412) | 2.552 (2.433) | 1.756 (1.741) | 1.766 (1.741) |

All within the run-to-run noise of this host; outputs are identical.

## Findings

### `-O0` vector code never translated

Clang marks `-O0` functions `optnone`, and `opt` skips its passes for them, the scalarizer and `expand-reductions` included. Before SV-02, `-O0` vector-extension code failed ("Vector arithmetic: `mul <16 x i8>`"); now it translates, with the reductions lowered by the backend.

### `scalarizer<min-bits=128>` does not legalize comparisons

The survey proposed LLVM's scalarizer with `min-bits=128` as the legalizer for wide vectors. It splits a vector only when its element type packs into the minimum, and an `i1` element packs 128 to a fragment, so `icmp <8 x i32>` and `select <8 x i1>` stay whole and are surrounded by concatenating shuffles. The backend therefore fragments every operation itself and the scalarizer is not used for typed code.

### A vector result and NativeAOT tail calls

.NET's x64 ABI passes a `Vector128` argument by value on the stack (System V) and returns one through a hidden buffer, where the previous explicit-layout value of 16 bytes travelled in two general registers. With typed results, `tests/musttail.sh` crashed under NativeAOT: the `<4 x float>` chain of `tests/ir/musttail-values.ll` calls through a function pointer, and ILC compiles that `tail. calli` with its fat-pointer check and a local return buffer, as two ordinary calls (`ilc --codegenopt JitDisasm`), so the million-call chain overflowed the stack. A direct call, and a call through a pointer with a vector argument and a scalar result, stay `tail.jmp`. Results of at most 16 bytes therefore return as their bytes, as before, and are converted at `ret` and after the call; parameters stay typed. A micro-benchmark of 100 million calls of a `float _Complex` multiplication and a four-float structure addition takes 1.40 s typed against 1.90 s scalarized under the JIT, so typed parameters cost nothing.

### Containers and the cost rule

The x86-64 ABI passes and returns small structures of floats as `<2 x float>` pairs: Bullet's `btVector3` is `{ <2 x float>, <2 x float> }`, and its IR mentions `<2 x float>` 6,496 times, against 86 vector arithmetic instructions that InstCombine formed from pairs of float operations. Typed, Bullet ran 5% slower under the JIT; scalarizing the functions with no vector arithmetic still left 4%; scalarizing every function body while keeping the typed boundaries was level with the scalar build, so the cost was the functions with a few two-lane operations amid inserts and extracts. The rule now in `VectorEmitter.Legalize`: a function stays typed when its operations save at least as many scalar operations (lanes minus one each) as its inserted and extracted lanes cost (one each). It scalarizes 119 of Bullet's functions, keeps vector-loops and Clang's vector extensions typed, and brings Bullet level (table above).

### The sysroot carries vector IR

`llvm-dis` over the portable sysroot archives finds vector types in 23 musl members (the `float complex` functions, whose ABI type is `<2 x float>`), libc++'s `locale.o` (`<32 x i8>`), libomp's `kmp_atomic.o` (`<32 x i1>`, `<32 x i32>`), eight flang-rt members (complex `<2 x float>` in `matmul`, `dot-product` and the reductions) and compiler-rt's `mulsc3` and `divsc3`. A program that links one of them gets different bytes from SV-02 on, without any option of this workstream: every C++ program that uses iostreams does. M11's exit criterion and ADR-16's consequence say such programs stay byte-identical; that holds for programs whose lowered IR has no vector types, and the benchmark set shows the speed unchanged for the others. The owner reworded it ([decisions](../plan/decisions.md#decided-by-the-owner-2026-11-03-sv-02)): programs whose IR has no vector operations stay byte-identical, and no program of the benchmark set may be slower.

## SV-03: the generic intrinsics

SV-03 lowers the remaining integer intrinsics and moves the reductions into the backend:

- `ctpop`, `ctlz`, `cttz` and `bitreverse` per lane width are compositions of shifts, masks and adds in `LlvmNet.Runtime.Vectors` (.NET has no vector population count, and no x86 ISA below AVX-512 BITALG does), `bswap` a constant byte `Shuffle`; a zero lane counts as the lane width, the defined result when the zero-is-poison flag is clear.
- `fshl`/`fshr` by a count shared by all lanes compute `high << s | (low >>> 1) >>> (width - 1 - s)` with `s = count mod width`, which never shifts by the whole width (.NET masks shift counts, so a shift by the width would be a shift by zero); a count per lane goes lane by lane.
- `fptosi.sat` and `fptoui.sat` use Vector128's saturating conversions, which map NaN to zero as LLVM defines (checked: `ConvertToInt32` of NaN, 3e9 and -3e9 gives 0, `int.MaxValue`, `int.MinValue`), then `NarrowWithSaturation`, unsigned for `fptoui.sat`.
- `vector.reverse`, `vector.insert` and `vector.extract` go through the shuffle lowering, which now builds a result from any lane map.
- Every `llvm.vector.reduce.*` is lowered by the backend: a power-of-two vector combines fragment j with j + count / 2 and then the halves of the last fragment by shuffles, the order `expand-reductions` and LLVM's x86 backend use, so reassociating floating-point reductions round as natively; integer sums of whole fragments use `Vector128.Sum`; other lane counts fold lane by lane. `expand-reductions` now runs only on the functions the scalarizer takes.

`tests/c/vector-extensions.c` gained the bit counts and reversals of every lane width (with zero lanes) and funnel shifts by shared, wrapping and per-lane counts; `tests/ir/vector-lowering.ll` gained `bswap`, the three `vector.*` intrinsics, both saturating conversions at narrower widths with NaN and out-of-range inputs, `ctlz`/`cttz` of zero, and reductions of three and two lanes and with NaN. Both match native at every level, typed and by default, with hardware intrinsics off and as NativeAOT. `tests/support-matrix.sh` regenerated the matrix: `llvm.vector.reverse` moves from rejected to translated, ten rows are new, and `llvm.vp.add` is rejected with its name, as `llvm.get.active.lane.mask` is.

## SV-04: the vector semantics probes

`tests/semantics/vectors.py` instantiates the scalar probe families of `tests/semantics/generate.py` per element type and lane count: i8 in 8, 16, 32 and 64 lanes, i16 in 4 to 16, i32 in 2, 3, 4 and 8, i64 in 1, 2 and 4, float in 2, 3, 4, 8 and 16, double in 2 and 4. Each probe is a noinline optnone function that loads its operands from constants (integer vectors of bit patterns, so NaN payloads pass unchanged) and stores its result, which lets 256- and 512-bit vectors through where llvmnet's integers stop at 128 bits. The families: integer arithmetic, division and remainder, shifts by counts at and above the lane width and by splats, all comparisons as bytes and as mask bits, masks combined and selected, `zext`/`sext`/`trunc` between every width, the bit-count and byte-swap intrinsics, min/max, saturating arithmetic, funnel shifts by per-lane and splat counts, every integer reduction, random shuffles to other lengths, a lane read and written at a run-time index, bitcasts to other lane widths; floating-point arithmetic and `frem`, `fneg`, `fabs`, `copysign`, all 16 predicates, rounding and `sqrt`, `minnum`/`maxnum`/`minimum`/`maximum`, `fma`, `fmuladd`, `fptosi`/`fptoui` and their saturating forms to every integer width, `sitofp`/`uitofp` from every width, `fpext`/`fptrunc`, and ordered, reassociating and min/max reductions. Lanes take the scalar edge values. A lane that is poison (a shift by the lane width or more, an out-of-range `fptosi`) is frozen and masked to zero, so a poison lane must not disturb its neighbours, and division by zero, which is undefined behavior, is never generated.

The module pair has 22,377 cases in 1,541 probes (`vector-integer` 13,144, `vector-float` 9,233). With seed 20261103 they print what the natively compiled modules print, byte for byte: in system and portable mode with `--vector-lowering=typed` (nothing scalarized), in portable mode with the default lowering (which scalarizes the lane-access probes), with `DOTNET_EnableHWIntrinsic=0`, with `DOTNET_EnableAVX=0` and as NativeAOT. `tests/vector-semantics.sh` runs all of that in 22 s. The portable images, copied with the native output, match on Windows x64 (`fluorite`), macOS ARM64 (`mainmac`) and Linux ARM64 (the arm64 runtime container under qemu), each with and without hardware intrinsics, so NEON's comparison, conversion and minimum rules agree with x64's. `tests/vector-fuzz.sh`, in the nightly fuzz job, repeats the run for ten seeds that change the random lanes, reduction inputs and shuffle masks; seeds 1 to 4 passed. No case mismatched in any of these runs.

## SV-05: autovectorization

`scripts/benchmark.py --vectorized` adds three engines that build the same sources with `-O2 -fvectorize -fslp-vectorize` on both compilers (`--vector-flags` replaces the flags): `native-vec`, whose ratio is to the scalar native build, so it is native's own vectorization gain, and `jit-vec` and `aot-vec`, whose ratios are to `native-vec`. The ffmpeg decoder keeps only its scalar engines. ptrdist-ft, whose output depends on glibc's `random` and is checked against native musl, failed in the first run because its glibc-linked vectorized native build was compared with musl too; the harness now compares only the llvmnet engines with the oracle.

### What the first run found

The first run, with the compiler at `ee9304b`, put the vectorized JIT at 2.22 and the vectorized NativeAOT at 1.54 times vectorized native, against 1.95 and 1.32 for the scalar builds: vectorizing made llvmnet 9 to 12% slower where it made native 3% faster. Three causes, each fixed:

- **oourafft, 14 times slower vectorized.** In `cftmdl` (1,201 locals) RyuJIT left 500 `Vector128.Add`, `Multiply`, `Subtract`, `Xor`, `GetLower`, `GetUpper` and `Create` calls uninlined at full optimization. .NET 10 implements the element-wise methods with the `Vector128<T>` operators, and RyuJIT reaches its intrinsic expansion only by inlining, which it stops at about 920 locals. A probe calls every public `Vector128` and `Vector256` method, instantiated for eight lane types, from a dynamic method of 1,000 locals and lists those that stay calls: `Add`, `Subtract`, `Multiply`, `Divide`, `BitwiseAnd`, `BitwiseOr`, `Xor`, `Negate`, `OnesComplement`, the three shifts, `CopySign`, `Widen`, `Clamp`, the `All`/`Any`/`Count`/`IndexOf` family, the `Vector64` halves (`GetLower`, `GetUpper`, `WithLower`, `WithUpper`, `Create` of halves) and integer division and `Sqrt`. The operators, comparisons, `ConditionalSelect`, `Shuffle`, `Min`/`Max`, `Narrow`, `WidenLower`/`WidenUpper`, the conversions, `GetElement`, `WithElement`, `Create` of lanes and `As` stay intrinsics. The emitter now calls the operators for the element-wise methods, lowers `copysign` to a sign-bit `ConditionalSelect`, and combines the 64-bit halves of 128-bit fragments as lanes of a `Vector128<double>` (`GetElement` and `Create`: `unpcklpd`/`unpckhpd` on x64) instead of through `Vector64`, which x64 does not accelerate and RyuJIT builds through memory. oourafft went from 17.9 to 1.33 s (1.54 s with its vectors scalarized).
- **stepanov-abstraction, 7 times slower vectorized.** Its vectorized `main` has 1,499 locals, past the 1,024 RyuJIT keeps in registers. That is BE-33, now done for locals: values whose live ranges do not meet share one ([evidence](2026-11-04-be33-local-sharing.md)). 12 to 17 s became 1.73 s (1.70 s scalar).
- **perlin, 2.6 times slower as baseline NativeAOT.** Its loop builds `<2 x i32>` vectors with `insertelement`. Without SSE4.1 (`pinsrd`), which ILC's default `x86-64` level does not assume, `WithElement` of a 32-bit lane stores the vector, writes the lane and reloads it, and the next insert's load waits for the store. A chain of insertelements at constant lanes is now one `Create` of its lanes (`movd` and `punpckldq` at SSE2), or over a non-constant vector one `WithElement` per lane without the intermediate vectors. Baseline NativeAOT went from 2.90 to 0.88 s (1.02 scalar, 0.83 at `x86-64-v3`). A NativeAOT probe of the other operations the vectorizers emit for the portable target (`WidenLower` of signed and unsigned lanes, `Narrow`, unsigned `LessThan`, `ConditionalSelect`, constant `Shuffle` of doubles and 32-bit lanes, `ConvertToDouble` of `long`, `Create`, `GetElement`) found each compiled to SSE2 sequences without calls at the baseline. `FusedMultiplyAdd` calls the C runtime's `fma` per lane there, which is why `fmuladd` stays a multiply and an add.

### The benchmark set at `-O2`

With the fixes and BE-33, the 58 programs validate in all six engines. In the final run (3 runs each), the vectorized builds stand at 1.97 (JIT) and 1.34 (NativeAOT) times vectorized native, against 1.94 and 1.32 for the scalar builds against scalar native (53 programs vectorized, 57 scalar); the first run's were 2.22 and 1.54. Vectorization gains, scalar time over vectorized time per engine (geometric means; above 1 is faster):

| | native | JIT | NativeAOT |
| --- | ---: | ---: | ---: |
| all 53 programs | 1.031 | 1.009 | 1.006 |
| programs running at least 0.2 s in that engine | 1.021 (41) | 1.015 (47) | 1.013 (42) |
| the same, in the run before the final one | 1.018 | 1.026 | 1.014 |
| linpack | 1.94 | 1.82 | 1.64 |
| perlin | 1.46 | 1.34 | 1.24 |
| lpbench | 1.31 | 1.03 (1.36 in the run before) | 1.25 |
| oourafft | 1.28 | 1.00 | 0.99 |
| tramp3d | 1.06 | 0.87 | 1.03 |
| fftbench | 0.79 | 0.90 | 0.94 |
| dhrystone | 0.98 | 0.84 | 0.95 |
| olden-bh | 0.62 | 0.96 | 0.63 |

The set is mostly pointer-chasing integer code, so vectorizing changes little for native code either; llvmnet now gains about what native gains, and on the numeric loops (linpack, perlin, lpbench) 1.2 to 1.8 times. The losses are mostly native's own (olden-bh, fftbench) or layout effects: dhrystone's vectorized JIT image ran 0.209 s against 0.156 s scalar, and the same IR translated again, with its functions and globals in another order, 0.166 s. tramp3d's JIT loss (its vectorized NativeAOT build gains) was not investigated.

### Vectorizer settings

The 26 programs where vectorizing changes anything, measured again with the loop vectorizer only (`-fno-slp-vectorize`) and without interleaving (`-mllvm -force-vector-interleave=1`), with the run before the final one and its compiler (local sharing in every function), gains against that run's scalar builds, host-normalized, programs running at least 0.2 s:

| flags | native | JIT | NativeAOT |
| --- | ---: | ---: | ---: |
| `-fvectorize -fslp-vectorize` (Clang's `-O2`) | 1.036 (17) | 1.058 (21) | 1.024 (18) |
| `-fvectorize -fno-slp-vectorize` | 1.041 | 1.038 | 1.052 |
| `... -mllvm -force-vector-interleave=1` | 1.048 | 1.056 | 1.051 |

The same native scalar build varies by about 4% per program between these runs, so differences of 2 to 3% in geometric means of about 20 programs are not significant, and each setting loses somewhere: without SLP perlin keeps none of its gain (1.01 in every engine), without interleaving tramp3d (0.74 JIT) and lpbench (1.14 JIT) lose theirs. No llvmnet-specific setting is better than Clang's own, so the proposal keeps them. `-mprefer-vector-width` does not apply to the portable target's SSE2 cost model, whose vectors are 128 bits; it pairs with `--isa=x86-64-v3` and `--vector-width=256` (SV-08, SV-09).

### `-O3`

The vectorized engines at `-O3 -fvectorize -fslp-vectorize` against the final run's scalar `-O2` builds gain 1.031 natively, 0.989 under the JIT and 1.028 as NativeAOT (programs running at least 0.2 s). The JIT's loss is linpack (0.41): at `-O3` its `main` absorbs the kernels and reaches 60,972 bytes of IL, past RyuJIT's 60,000-byte limit, and runs as MinOpts (PF-07), while NativeAOT, which never falls back, gains 1.68 on the same IR. `-O3` adds nothing for vectorization that `-O2` does not.

### The survey's kernels

The seven kernels of the [SIMD survey](2026-11-03-simd-survey.md#hand-written-vectors-against-scalar-and-native-code) (`artifacts/simd-survey/autovec/kernels.c`, `-ffp-contract=off`), scalar at `-O2 -fno-vectorize -fno-slp-vectorize` and vectorized at `-O3 -fvectorize -fslp-vectorize`, `scripts/ab-time.py 5`, median seconds:

| kernel | native scalar | native SSE2 | JIT scalar | JIT vectorized | JIT vectorized, `ee9304b` | NativeAOT scalar | NativeAOT vectorized |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| saxpy | 0.578 | 0.182 | 0.944 | 0.283 | 0.291 | 0.982 | 0.249 |
| i32mix | 0.710 | 0.227 | 0.865 | 0.268 | 0.320 | 0.833 | 0.368 |
| sad8 | 0.630 | 0.122 | 0.860 | 0.273 | 0.283 | 0.637 | 0.324 |
| dot16 | 0.656 | 0.145 | 1.076 | 0.219 | 0.220 | 1.027 | 0.418 |
| max8 | 0.345 | 0.038 | 2.451 | 0.075 | 0.081 | 2.422 | 0.062 |
| fdot | 0.656 | 0.312 | 0.784 | 0.362 | 0.365 | 0.838 | 0.326 |
| fdot8 | 0.671 | 0.172 | 0.752 | 0.258 | 0.241 | 0.835 | 0.298 |
| paeth | 0.593 | 0.663 | 0.832 | 0.768 | 0.767 | 0.789 | 1.030 |

Vectorized, the kernels other than paeth run 2.2 to 4.9 times faster than llvmnet's scalar builds under the JIT and 2.0 to 3.9 as NativeAOT (max8 33 and 39 times, because its scalar form is PF-18's select chain), at 1.2 to 2.2 times native SSE2 under the JIT and 1.04 to 2.9 as NativeAOT, close to what the survey's hand-written `Vector128` loops predicted (3.7 to 5 times on the element-wise and widening kernels). NativeAOT's baseline lags the JIT on i32mix, dot16 and sad8, whose 32-bit multiplications and byte widening it compiles without SSE4.1 (Level 2's `IsSupported` helpers); paeth, whose vectorized form is slower natively too, loses as NativeAOT. The kernels' functions are small, so this item's fixes change little for them (i32mix 0.320 to 0.268 s).

### What remains

The survey's library reproducers (Skia's nanobench subset, libjxl, dav1d, libjpeg-turbo and Opus) were not rebuilt with the vectorizers, and the SLP minimum register size was not varied separately (`-fno-slp-vectorize` bounds its effect). The decision on the default is the owner's ([proposal](../plan/decisions.md#proposed-for-the-owner-2026-11-04-sv-05)).

## SV-06: 256-bit fragments

`--vector-width=256` holds every vector in `Vector256<T>` fragments instead of `Vector128<T>`: the same lowering with twice the lanes per fragment, `Vector256`'s methods, and the 256-bit helpers of `LlvmNet.Runtime.Vectors` (by halves where `Vector256` has no operation). A reduction first combines a fragment's 128-bit halves, the same step, so its shuffles are `Vector128` ones. The vector probes (22,377 cases) match native at 256 bits under the JIT, with `DOTNET_EnableAVX=0`, with `DOTNET_EnableHWIntrinsic=0`, and as NativeAOT at the default instruction set and at `x86-64-v3`; so does the vector-extension fixture.

The survey's seven kernels (`artifacts/simd-survey/autovec/kernels.c`, five times the repetitions of the survey), vectorized by Clang for AVX2 (`--runtime=system -O3 -march=x86-64-v3 -ffp-contract=off -fvectorize -fslp-vectorize`), timed with `scripts/ab-time.py 3`, seconds (median; the JIT's startup is about 0.035 s, NativeAOT's 0.003 s):

| Kernel | native AVX2 | JIT 128 | JIT 256 | NativeAOT `x86-64-v3` 128 | NativeAOT `x86-64-v3` 256 | JIT 128 without AVX | JIT 256 without AVX |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| saxpy | 0.468 | 1.119 | 0.631 | 1.257 | 0.584 | 1.196 | 1.236 |
| i32mix | 0.543 | 1.154 | 0.595 | 1.960 | 0.554 | 2.463 | 2.260 |
| sad8 | 0.272 | 1.313 | 0.822 | 1.409 | 0.792 | 1.330 | 1.215 |
| dot16 | 0.308 | 1.107 | 0.693 | 1.310 | 0.665 | 1.171 | 1.192 |
| max8 | 0.100 | 0.216 | 0.146 | 0.237 | 0.112 | 0.212 | 3.676 |
| fdot | 0.611 | 0.649 | 0.676 | 0.647 | 0.664 | 0.651 | 0.709 |
| fdot8 | 0.745 | 0.972 | 0.775 | 1.013 | 0.798 | 0.968 | 1.057 |

256-bit fragments gain 1.25 to 1.9 times over 128 where the host has AVX2, and put NativeAOT `x86-64-v3` at 1.02 to 1.25 times native AVX2 on the element-wise and float kernels; `sad8` and `dot16` stay 2.2 to 3 times native, the `psadbw`/`pmaddwd` idioms of SV-21. Without AVX most kernels run as at 128 bits, but not `max8`: there RyuJIT does not treat `Vector256` methods as intrinsics, and in `k_max8`, which LLVM unrolled into 3,200 instructions, it leaves 147 `As` and 73 `Max` calls uninlined (`DOTNET_JitDisasm`), 14 times slower than 128. On macOS ARM64 (`mainmac`, portable `-O3 -fvectorize`, 128-bit vectors), where `Vector256` is never accelerated, 256 costs 1.07 to 1.5 times 128 (saxpy 1.182 against 0.939 s, sad8 2.042 against 1.336). `--vector-width=256` is therefore for builds that target AVX2 hosts, as the plan intends with `--isa=x86-64-v3` (SV-08, SV-09), and 128 stays the default. The same ARM64 run puts the 128-bit JIT code at 1.5 times native NEON on saxpy, i32mix and max8.

## What it does not show

- Speedups on the survey's libraries: SV-05 measured the benchmark set and the survey's kernels; the vectorizers stay off by default (ADR-11) until the owner decides.
- Transcendental intrinsics and `is.fpclass` still scalarize their function (SV-03); SIMD128 on the typed lowering is SV-07.
- The disassembly is from this AVX-512 host; NativeAOT's default instruction set (SSE2 guaranteed) compiles byte shuffles and 32-bit multiplications through software sequences, which Level 2's explicit `IsSupported` helpers address.
- Linux ARM64 NativeAOT, for the reason above.
