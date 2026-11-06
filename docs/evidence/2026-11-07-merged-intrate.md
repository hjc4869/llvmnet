# Evidence: SPECrate 2017 integer and x264 after the 2026-11-07 merges, against native with mimalloc

Host: AMD Ryzen AI MAX+ 395 (Zen 5, 16 cores, 32 threads), 124 GB, Linux 6.12.115, .NET 10.0.14 (ILCompiler 10.0.14), clang, clang++ and flang 22.1.8; llvmnet links the same `libLLVM.so.22.1`. Branch `x264-perf` at `22cb776`. A development measurement with the licensed kit (`artifacts/spec2017-harness`), not a reportable SPEC result: refrate, one copy, three iterations, `--noreportable`; runcpu marks both runs invalid only for the flags it does not know, as on 2026-11-06.

The owner asked for every known performance item to be worked on in parallel and merged into this branch one at a time, for mimalloc as the toolchain's default allocator over .NET memory alone, and then for the benchmarks again, with the native baseline linked against the same mimalloc release compiled natively. The 2026-11-06 measurements this page compares with are [2026-11-06-spec-intrate.md](2026-11-06-spec-intrate.md) and [2026-11-06-x264.md](2026-11-06-x264.md).

## What was merged

Six branches, each from `22a0f55`, merged in this order. After each merge the main checkout was fast-forwarded to it and ran the core gate (`tests/run.sh --tag core`):

| Merge | Branch | Items | Evidence | Core gate |
| --- | --- | --- | --- | --- |
| `fdbb648` | `claude/mimalloc-a9521a` | PF-25: mimalloc 3.6.1 in the portable C library, memory from `NativeMemory` only | [mimalloc](2026-11-07-mimalloc.md) | 105 of 105 |
| `23cf6f6` | `claude/phi-coalescing-4bf36c` | PF-22 phi coalescing, PF-23 constant-offset GEPs at their uses | [phi coalescing](2026-11-07-phi-coalescing.md) | 106 of 106 |
| `5f4a47c` | `claude/call-overhead-33b510` | PF-24 frame slots, PF-26 no type-initializer check as NativeAOT, PF-27 inlined `RawCalliHelper` stubs, PF-28 x264's motion search | [call overhead](2026-11-07-call-overhead.md) | 108 of 108 |
| `e298a31` | `claude/v3-fragments-dd92fa` | SV-41 fragments sized per value at 256 bits, PF-18 loop-carried selects | [v3 fragments](2026-11-07-v3-fragments.md) | 108 of 108 |
| `94ba3c8` | `claude/arm64-lowering-0fdc00` | SV-34 to SV-39, ARM64 forms of the vector lowering | [ARM64 lowering](2026-11-07-arm64-lowering.md) | 108 of 108 |
| `267916a` | `claude/strided-gathers-8afd04` | SV-40 strided gathers as wide loads and a shuffle | [strided gathers](2026-11-07-strided-gathers.md) | 108 of 108, with the two commits below |

The arm64 and strided merges had conflicts with SV-41 in `VectorEmitter.cs`, `Vectors.cs` and `TypeSystem.cs`: SV-41 replaced the module-wide fragment width with each value's, so the ARM64 forms and the strided shuffles now take the width of the value they lower and apply only to 128-bit fragments, as their branches assumed. `Vectors.ShuffleBytes` gained a form that uses SV-38's ARM64 permutes when the pattern has one and `pshufb` otherwise.

Two commits after the merges:

- **`df9a396`, frame-slot GEPs.** PF-23 computes a constant-offset GEP at each use when its base is an argument or has a local, and PF-24 gives a static alloca a frame slot without a local, so with both merged the field addresses of a stack structure each kept a local again. Such a GEP is now the slot's address plus the offset at each use, which RyuJIT folds into the access, unless the slot's alignment is more than 8 and rounds the address.
- **`22cb776`, `tests/x86-intrinsics.sh`.** The macro checks piped clang's preprocessor output into `grep -q`, which exits at the first match; under load clang's next write failed with a broken pipe, which `pipefail` reported as a failure. The checks now read all of the output.

**ARM64.** The cross-platform bundle (`tests/xp`) built from `22cb776` passed on `mainmac` (Apple M6, macOS 27.2): 91 passed, 0 failed, 4 skipped, and the strided-gathers fixture matched native there.

## The run end to end

The toolchain was packaged from `22cb776` (`scripts/package-toolchain.sh`, `artifacts/spec-portable-toolchain-merged`), and the whole suite was built from source with it (`--rebuild`, since the label is fixed) and run once, one iteration (CPU2017.695): every benchmark built, ran and validated, with no change needed. Its times were within 1% of the timed run below.

## Results

Native: clang, clang++ and flang 22.1.8 at `-O3` for the x86-64 baseline (SSE2), with the per-benchmark portability flags of 2026-11-06 and mimalloc 3.6.1 linked into every benchmark as a static override (`artifacts/x264-perf/spec/native-clang22-mimalloc.cfg`: `EXTRA_LIBS = artifacts/mimalloc-native/lib/mimalloc.o`, the release's own `MI_BUILD_OBJECT` with `MI_OVERRIDE=ON`, built with `clang-22 -O3` and mimalloc's default options; [how it was built and checked](2026-11-07-mimalloc.md#native-baselines)), still with glibc 2.43 and libstdc++ 16.2. llvmnet: `config/spec/llvmnet.cfg` at `-O3 -fvectorize -fslp-vectorize` (the typed lowering to `Vector128`), portable mode (musl 1.2.5 with mimalloc, libc++ 22.1.8), NativeAOT for x86-64-v2. One benchmark at a time, not pinned; the measurement started at 03:28, after the six branches' sessions had last been active at 01:58, and nothing else ran on the host. Native took 4,946 s (CPU2017.696), llvmnet 5,647 s (CPU2017.697). Seconds are SPEC's median of three:

| Benchmark | native, mimalloc (s) | llvmnet (s) | llvmnet / native | native runs | llvmnet runs |
| --- | ---: | ---: | ---: | --- | --- |
| 500.perlbench_r | 141 | 171 | 1.21 | 141, 140, 141 | 170, 171, 171 |
| 502.gcc_r | 116 | 139 | 1.20 | 116, 116, 116 | 139, 140, 139 |
| 505.mcf_r | 245 | 249 | 1.02 | 245, 246, 245 | 249, 249, 250 |
| 520.omnetpp_r | 196 | 198 | 1.01 | 197, 196, 182 | 198, 203, 191 |
| 523.xalancbmk_r | 94 | 100 | 1.06 | 94, 94.6, 93 | 100, 100, 101 |
| 525.x264_r | 81.3 | 85.4 | 1.05 | 81.3, 80.8, 81.3 | 85.4, 85.2, 85.4 |
| 531.deepsjeng_r | 190 | 221 | 1.16 | 190, 198, 190 | 221, 221, 222 |
| 541.leela_r | 232 | 248 | 1.07 | 232, 231, 232 | 249, 248, 248 |
| 548.exchange2_r | 108 | 156 | 1.44 | 108, 108, 108 | 157, 156, 156 |
| 557.xz_r | 212 | 225 | 1.06 | 212, 212, 211 | 224, 225, 226 |
| geometric mean | | | **1.12** | | |

Every run validated. The estimated SPECrate2017_int_base is 8.68 against native's 9.76.

### Against 2026-11-06

Each side against its own run of 2026-11-06 (native CPU2017.688 with glibc's allocator, llvmnet CPU2017.692 at `220c0f4` with mallocng), as the ratio of the medians:

| Benchmark | native: glibc (s) | native: mimalloc (s) | mimalloc / glibc | llvmnet: `220c0f4` (s) | llvmnet: `22cb776` (s) | `22cb776` / `220c0f4` |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 500.perlbench_r | 150 | 141 | 0.94 | 355 | 171 | 0.48 |
| 502.gcc_r | 125 | 116 | 0.93 | 164 | 139 | 0.85 |
| 505.mcf_r | 252 | 245 | 0.97 | 268 | 249 | 0.93 |
| 520.omnetpp_r | 236 | 196 | 0.83 | 388 | 198 | 0.51 |
| 523.xalancbmk_r | 118 | 94 | 0.80 | 114 | 100 | 0.88 |
| 525.x264_r | 83.8 | 81.3 | 0.97 | 99.1 | 85.4 | 0.86 |
| 531.deepsjeng_r | 193 | 190 | 0.98 | 247 | 221 | 0.89 |
| 541.leela_r | 232 | 232 | 1.00 | 281 | 248 | 0.88 |
| 548.exchange2_r | 109 | 108 | 0.99 | 155 | 156 | 1.01 |
| 557.xz_r | 228 | 212 | 0.93 | 254 | 225 | 0.89 |
| geometric mean | | | **0.93** | | | **0.80** |

mimalloc makes native 7% faster (estimated rate 9.10 to 9.76), most in the allocation-heavy C++ benchmarks, xalancbmk and omnetpp. llvmnet is 20% faster (6.92 to 8.68): perlbench and omnetpp take half their time, where the profiles of 2026-11-06 put phi copies in `S_regmatch` (PF-22, PF-23) and mallocng and the variadic call area's `localloc` in omnetpp (PF-25, PF-24); the others are 7 to 15% faster, which this run does not divide among the items (their pages measure each). Only exchange2 is unchanged. Against the glibc native of 2026-11-06 llvmnet now takes 1.05 times its time; against the same allocator on both sides, 1.12, from 1.31.

## x264

SPEC's 525.x264_r refrate workload 1 (`--pass 1`, 1,000 frames, 1280×720), built as in the [x264 evaluation](2026-11-06-x264.md) (`artifacts/x264-perf/build.sh`) with the same toolchain; the native builds link the same `mimalloc.o` (`LINK_EXTRA`, `nm` shows mimalloc's `malloc` and `free` in each executable). Three round-robin rounds, pinned to CPU 2 and its SMT sibling 18, run right after the SPEC runs on the same idle host; elapsed seconds, medians (`artifacts/x264-perf/results-merged.tsv`):

| Pair | native, mimalloc (s) | llvmnet (s) | llvmnet / native | 2026-11-06: native, llvmnet, ratio |
| --- | ---: | ---: | ---: | --- |
| scalar (`-fno-vectorize -fno-slp-vectorize`) | 22.47 | 25.78 | 1.15 | 22.67, 26.69, 1.18 |
| Vector128 against SSE2 | 12.06 | 12.49 | 1.03 | 12.40, 14.83, 1.20 |
| `x86-64-v3` against `-march=x86-64-v3` | 9.67 | 11.81 | 1.22 | 9.81, 13.55, 1.38 |

Each of the 18 runs wrote native's `BuckBunny_New.264` (md5 `225bcd8b…`) and `x264_stats.log` (`15c045d5…`). The runs within a configuration spread by less than 0.6%. mimalloc makes native x264 1 to 3% faster, and raises its peak resident size from 163 MB to 207 MB, as it does llvmnet's from 175 MB to 214 MB. llvmnet is 3% faster scalar, 16% at Vector128 and 13% at `x86-64-v3`; the items' pages measured SV-40 in the lowres filter, PF-24 and PF-27 in the motion search, and SV-41 and PF-18 at `x86-64-v3`.

## What remains

- **548.exchange2_r (1.44)**, now the largest gap, did not move and has not been profiled.
- **perlbench (1.21), gcc (1.20), deepsjeng (1.16).** perlbench's `S_regmatch` still copies the 395 of its 5,290 phi inputs that interfere with their phi or its class, mostly induction variables' next values and values computed from a member ([phi coalescing](2026-11-07-phi-coalescing.md)); gcc and deepsjeng have no profile after the merges.
- **x264 at `x86-64-v3` (1.22).** SV-40 is off for 256-bit fragments; with SV-41 merged, vectors of at most 128 bits are `Vector128` there, so the rewrite could apply to them ([strided gathers](2026-11-07-strided-gathers.md)). `x264_clip_uint8` keeps PF-15's mask where native code has a `cmov` ([v3 fragments](2026-11-07-v3-fragments.md)).
- **PF-23's two regressions.** fftbench (1.08) and richards (1.08) as NativeAOT, small hot loops whose field addresses are now folded into their loads instead of kept in registers ([phi coalescing](2026-11-07-phi-coalescing.md)).
- **From mimalloc.** An empty `asm` statement without a `memory` clobber still becomes a full barrier in the compiler (the mimalloc patch leaves its one out of the fast path); C thread-local accessors are calls that RyuJIT does not inline; threads that a host library starts and that call back through a native extension (NX-08) keep their heaps when they end ([mimalloc](2026-11-07-mimalloc.md)).
