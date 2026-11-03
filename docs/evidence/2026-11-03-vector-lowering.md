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

## What it does not show

- Speedups on real programs: the vectorizers stay off by default (ADR-11), and measuring the benchmark set and the survey's libraries with them on is SV-05. The kernels of the survey were not rerun.
- The bit-count, byte-swap, funnel-shift and other generic intrinsics still scalarize their function (SV-03); the vector forms of the semantics probes (SV-04), `Vector256` (SV-06) and SIMD128 on the typed lowering (SV-07) are separate items.
- The disassembly is from this AVX-512 host; NativeAOT's default instruction set (SSE2 guaranteed) compiles byte shuffles and 32-bit multiplications through software sequences, which Level 2's explicit `IsSupported` helpers address.
- Linux ARM64 NativeAOT, for the reason above.
