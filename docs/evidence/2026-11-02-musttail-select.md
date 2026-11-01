# Evidence: BE-44 musttail calls and PF-15 branch-free selects (2026-11-02)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang 22.1.8, starting from commit `a5ab005`. Other sessions shared the host (load average between 1 and 65 from Skia and ANGLE links and gates in other checkouts), so timings are medians or minima of repeated runs, before/after pairs ran back to back or alternated, and the benchmark harness normalizes by the native times it measures alongside. Heavy runs used memory-capped user scopes (`systemd-run --user --scope -p MemoryMax=...`).

The open-source library survey (2026-11-01/02, `artifacts/oss/REPORT.md`, round two; recorded in [2026-11-02-library-survey.md](2026-11-02-library-survey.md)) found two translation problems, now plan items BE-44 and PF-15: `musttail` calls became ordinary calls (survey R1, Skia's DM aborts with a stack overflow), and integer `select`/`llvm.abs` became branches that RyuJIT keeps in loops (survey R2, PNG's Paeth filter 3.4–5.7 times native time). Both reproducers are in the survey's `results/skia-repro/musttail/` and `results/libpng-repro/paeth-select/`.

## BE-44: musttail (survey R1)

**What the runtimes guarantee.** From the .NET 10 sources (`release/10.0`): RyuJIT honors an explicit `tail.` call either as a *fast* tail call, which reuses the caller's frame, or through the runtime's tail-call helpers, which store the arguments, return and call from a dispatcher (`morph.cpp`, `fgMorphPotentialTailCall`). A fast tail call is refused when the caller uses `localloc`, when it needs a GS cookie (x64; only `localloc` or unsafe buffers cause one), when the callee needs more stack-argument space than the caller received, when the callee has a return buffer and the caller does not, or when an argument is a struct copied to the frame and passed by reference (Windows x64, ARM64) (`fgCanFastTailCall`). Return types that are not tail-call compatible make it an ordinary call silently (`importercalls.cpp`). NativeAOT's `getTailCallHelpers` returns false ("Slow tailcalls are not supported yet", dotnet/runtime#35423, `CorInfoImpl.cs`), so under NativeAOT anything but a fast tail call becomes an ordinary call. Fast tail calls are allowed at every optimization level (`compiler.cpp`).

**Design.** A `musttail` call becomes `tail. call` or `tail. calli` followed by `ret`. LLVM requires the caller's and callee's prototypes to match, so the stack-argument and return-buffer conditions always hold; what remains is `localloc` and by-reference structs:

- A function with a `musttail` call takes its allocas and its variadic call area from the per-thread alloca arena (`StackMemory`, BE-17) instead of `localloc`, inside the arena's try/finally. Its tail calls `leave` the protected region to a call site after it, so the `finally` releases the arena first; LLVM does not let a `musttail` callee use the caller's allocas. Functions with `setjmp` sites get the same treatment for their dispatcher's try/catch.
- Calls a fast tail call cannot carry are rejected at compile time: `byval` arguments (llvmnet copies them in the callee, from memory the tail call releases), arguments or results of more than 16 bytes (by-reference structs on Windows x64 and ARM64), and forwarded variadic arguments. A `byval` parameter always means a `byval` argument, because LLVM requires the call's ABI attributes to match the caller's. Values of up to 16 bytes that llvmnet passes as value types (vectors at function boundaries, `long double`, `i128`) are accepted: Linux x64 and ARM64 pass them in registers or by value, so the tail call is fast there. Windows x64 passes them by reference, so a NativeAOT executable there makes such a call an ordinary call; the JIT still guarantees it through its helpers. A first version rejected every non-scalar argument, which turned out to stop Skia from compiling at all: skcms, Skia's color-management code, chains its stages with `musttail` calls that pass four `<4 x float>` values.
- In system mode a `musttail` call to a native function, direct or through a pointer that resolves to native code, stays an ordinary call: native frames are outside the CLR's tail-call machinery.
- Ordinary `tail` calls keep compiling as ordinary calls (see the measurement below).

Skia's raster pipeline, the case that failed, has 293 functions with `musttail` calls in `core.SkOpts.o` (`llvm-dis` of the survey's portable objects), all `void (ptr, ptr, float, float, float, float)` through a function pointer, none with an alloca, `byval` or variadic arguments: they take the plain path, with no arena. skcms adds `musttail` chains with `<4 x float>` arguments (the 16-byte case above).

**Ordinary `tail` calls stay ordinary.** Clang marks most calls in return position `tail`, which only says the callee does not use the caller's allocas. Putting `tail.` on them was measured with an experimental switch in a scratch tree (not committed) that emitted `tail.` for every `tail` call in return position the `musttail` path could carry. RyuJIT never inlines a call with an explicit tail prefix (`CALLSITE_EXPLICIT_TAIL_PREFIX`, `importercalls.cpp`), so a loop calling a two-line wrapper ran 2.0 times as long (0.089 s to 0.177 s, median of five; `int wrapper(int x) { return leaf(x + 1); }`); and under the JIT a caller with `localloc` would take the slow helper path. Over the micro and suite groups of `scripts/benchmark.py` the experiment cost 1.7% under the JIT and 0.9% as NativeAOT (geometric means against the run without it), up to 1.23 and 1.34 times on lame, 1.19 and 1.12 on olden-health and 1.16 (NativeAOT) on ptrdist-anagram, while mutex came out faster (0.84, 0.91). RyuJIT already makes opportunistic tail calls where it can, without the prefix, so the prefix only adds the guarantee, which ordinary `tail` calls do not ask for.

**Results** (final commit `fd861fd`). The survey's reproducer (`results/skia-repro/musttail/run.sh`: 1,000,000 `musttail` calls through a function pointer with four `float` arguments), with the 8 MB default stack:

| Build | 1,000,000 calls | 100,000,000 calls |
| --- | --- | --- |
| native clang-22 -O2 | 500000500010 | – |
| before (`a5ab005`), JIT | "Stack overflow.", exit 134 | exit 134 |
| before, NativeAOT | no output, exit 139 (SIGSEGV) | exit 139 |
| after, JIT | 500000500010, exit 0 | 5000000050000010, exit 0 (0.6 s) |
| after, NativeAOT | 500000500010, exit 0 | 5000000050000010, exit 0 |

Skia's DM, translated by commit `8ff348b` from the survey's bitcode-linked `dm.bc` (musttail on, `SK_CPU_PPC` not defined): translation 24.8 s at 2.0 GB, then `dm --src tests --nogpu --threads 4 --config 8888` runs all 2,197 unit tests to "Finished!" in 29.7 s, exit 0, no failures; before, it aborted after 2,017 with the stack overflow in `SkSLIntegerDivisionES3_RP`, which now passes on its own as well (`--match '^SkSLIntegerDivisionES3_RP$'`). The survey needed `-DSK_CPU_PPC` (musttail off) for the same 2,197.

Tests:

- `tests/musttail.sh` (core, aot): `tests/c/musttail.c` runs 3,000,000-deep chains (a direct mutual recursion, an indirect chain with `float` arguments, and callers with an alloca, a variadic call and a `setjmp`, so the arena and protected-region paths are covered) and must print what the native build prints, in both modes at -O0 and -O2 under the JIT, and as NativeAOT (portable at -O0 and -O2, system at -O2). It checks that the five functions keep `tail. call` or `tail. calli` followed by `ret` in CIL, with the new `LlvmNet.Checks il-opcodes` command. `tests/ir/musttail-values.ll` chains 1,000,000 calls with `<4 x float>` through a pointer, `i128` and `x86_fp80`, under the JIT (`tests/ir.sh`) and NativeAOT (here). Three fixtures in `tests/ir/rejected/` must fail with their diagnostics: a `byval` argument, forwarded variadic arguments and a `<8 x float>` argument.
- The support matrix gains six rows: ordinary `tail`/`notail` calls and `musttail` calls (direct, indirect, from a frame with an alloca) and with 16-byte values are translated; the three rejected forms show their diagnostics, also in `docs/user/limitations.md`.

## PF-15: select, abs and integer min/max (survey R2)

**Why RyuJIT keeps the branches.** `ifconversion.cpp` (`OptIfConversionDsc::optIfConvert`, .NET 10) turns a branch diamond into a `SELECT` (a `cmov`) only when the block is not in a loop: it gives up when the block's weight exceeds 1.05 or the join block can reach the start block ("Skipping if-conversion inside loop"), apart from a stress mode. No other phase creates an integer `SELECT` on x64 (the one in `lowerxarch.cpp` is part of saturating float-to-integer casts), and integer `Math.Min`/`Max` are intrinsics only on RISC-V. So no branchy CIL shape becomes a `cmov` in a hot loop, which is where these selects matter.

**Lowering.** Integer and pointer selects are computed as `false ^ ((true ^ false) & mask)`, where the mask is all ones when the condition holds. When the condition is an ordered `icmp` of integers of at most 32 bits, the mask is the sign of their 64-bit difference (`sub` + `sar`), which replaces `setcc`, zero extension and negation and shortens the dependency chain; otherwise it is `-condition`. A boolean select is the `and` or `or` it stands for. `llvm.abs` is `(x ^ s) - s` with `s = x >> (bits - 1)`, which wraps the minimum to itself as LLVM specifies. `llvm.smin`/`smax`/`umin`/`umax` of two variables of up to 32 bits are `right + (d & s)` and `left - (d & s)`, with `d` the 64-bit difference and `s` its sign, and of wider ones the select form; against a constant bound they keep the branch (next section). Floating-point, 128-bit and aggregate selects keep the branch, which is exact for every value (NaN payloads, signed zeros).

**Clamps keep their branch.** The first version (commit `8ff348b`) made every min/max branch-free, and its benchmark run against the parent commit found FFmpeg's 10-bit HEVC decoding 9% slower under the JIT and 13% as NativeAOT. An A/B of the decode harness (the same linked `decode.bc` translated by each compiler, `scripts/ab-time.py 7`, `hevc-1080p10` single-threaded, median seconds), with the three lowerings of the `right + (d & s)` version switched back to branches one at a time in a scratch build (ranges over repeated A/B sessions):

| Lowering | NativeAOT | JIT |
| --- | ---: | ---: |
| before (`a5ab005`, all branches) | 2.39–2.43 | 2.89–2.98 |
| `8ff348b` (all branch-free) | 2.75 | 3.25 |
| min/max as `right + (d & s)` | 2.66–2.70 | 3.11 |
| select back to branches | 2.65 | |
| abs back to branches | 2.68 | |
| min/max back to branches | 2.35–2.36 | |
| min/max back to branches against a constant only | 2.35–2.37 | |
| the same, but `smax(x, 0)` as `x & ~(x >> 31)` | 2.49 | |
| final (`fd861fd`) | 2.36 | 2.84 |

FFmpeg clamps a 10-bit pixel with `smax(x, 0)` and then `umin(x, 1023)` (LLVM's form of `av_clip_uintp2`), comparisons that almost always go the same way; there a predicted branch costs less than any mask, even three instructions. Making every min/max a branch instead lost Paeth's `umin(pb, pa)`, a comparison of two data values (0.046 s to 0.108 s per pass on random data). So a min/max against a constant keeps its branch and a min/max of two variables does not; selects and abs, where branches did not help HEVC, stay branch-free. With the final rule HEVC decodes 1.4% faster than before as NativeAOT and 3% under the JIT, and H.264 2% faster as NativeAOT (1.716 s against 1.752 s).

**Results** (final commit `fd861fd`). The survey's reproducer (`results/libpng-repro/paeth-select/run.sh`: libpng's Paeth unfiltering over 24 MB per pass, best of five passes; three runs of each build alternated, the range given):

| Build | random bytes | smooth gradient | conditional jumps / cmov in `main` (JIT) |
| --- | ---: | ---: | --- |
| native clang-22 -O2, no vectorizer | 0.0325–0.0330 s | 0.0327–0.0328 s | 7 cmov in `main` (survey) |
| before (`a5ab005`), JIT | 0.195 s (6.0x) | 0.110–0.112 s (3.4x) | 13 / 1 |
| before, NativeAOT | 0.198–0.200 s (6.1x) | 0.112–0.114 s (3.4x) | |
| after, JIT | 0.046 s (1.41x) | 0.046 s (1.40x) | 8 / 0 |
| after, NativeAOT | 0.045 s (1.39x) | 0.045 s (1.38x) | |

The loop is branch-free now (`DOTNET_JitDisasm`; the remaining conditional jumps in `main` are loop control), about 40 instructions per byte against native's 30, and its time no longer depends on the data. Of the remaining 1.4x, `setcc`-based masks would cost about 11% more (the first draft ran at 1.55x), and RyuJIT reloads `row` and `prev` from their statics on every iteration because a byte store may alias them; making `paeth()` `noinline`, so that the pointers are arguments in registers, measured the same, so the reloads cost nothing measurable here.

In the libraries the survey measured: libpng's and Wuffs' portable bitcode (built by the survey with the `a5ab005` toolchain, `build/libpng-portable/liblibpng16.a` and `build/wuffs-portable/wuffs-decode.o`) relinked with this compiler, decoding the survey's 3840x2160 RGB photo re-encoded with each PNG filter type (`scripts/libpng-filters.sh`'s images; minimum of five in-process decodes, seconds; native and before are the survey's binaries, run in the same session):

| Decode | native | before NativeAOT | before JIT | after NativeAOT | after JIT |
| --- | ---: | ---: | ---: | ---: | ---: |
| libpng, none | 0.090 | 0.100 | 0.098 | 0.100 | 0.095 |
| libpng, sub | 0.104 | 0.112 | 0.110 | 0.116 | 0.114 |
| libpng, up | 0.095 | 0.105 | 0.105 | 0.109 | 0.106 |
| libpng, avg | 0.103 | 0.116 | 0.117 | 0.119 | 0.112 |
| libpng, paeth | 0.123 | 0.272 (2.20x) | 0.276 (2.24x) | 0.145 (1.18x) | 0.144 (1.17x) |
| libpng, mixed (per-row choice) | 0.109 | 0.157 (1.44x) | 0.160 (1.47x) | 0.127 (1.17x) | 0.120 (1.10x) |
| Wuffs, none | 0.088 | 0.093 | 0.093 | 0.094 | 0.093 |
| Wuffs, paeth | 0.130 | 0.261 (2.01x) | 0.264 (2.03x) | 0.123 (0.95x) | 0.123 (0.95x) |
| Wuffs, mixed (per-row choice) | 0.093 | 0.136 (1.45x) | 0.134 (1.44x) | 0.101 (1.08x) | 0.100 (1.07x) |

The Paeth filter now costs about what the other filters cost (1.0–1.2x), so the survey's PNG decode gap in libpng and Wuffs is closed. The filters without selects move by at most 4% (NativeAOT slightly slower, the JIT mostly faster), the size of the NativeAOT layout effect described under Benchmarks. Across libpng's 369 methods the IL of `8ff348b` was 0.3% smaller than before, and `png_read_filter_row_paeth_multibyte_pixel` has 13 branch instructions instead of 22 (the rest are loop control).

Tests:

- `tests/select.sh` (core, aot): `tests/c/select.c` runs selects under every `icmp` predicate, min/max and abs on every pair of 16 edge values at widths of 8, 16, 17, 32, 33 and 64 bits, signed and unsigned, pointer selects, selects on a loaded flag, `&&`/`||` and floating-point selects with NaN, signed zeros, infinities and a denormal, and must print the native build's hashes in both modes at -O0 and -O2 under the JIT and as NativeAOT. It checks that the source still produces each kind of select and intrinsic, and that four kernels (libpng's Paeth predictor, a 64-bit clamp, a select on a flag, a 64-bit select) have no branch in CIL. Swapping the operands of one predicate in the compiler made the hashes differ from the second group on.
- `tests/semantics.sh` already compares `llvm.abs`, `smin`, `smax`, `umin` and `umax` bit for bit with native code at many widths (45,310 probes pass).

## Benchmarks

`python3 scripts/benchmark.py --jobs 12` (all 58 benchmarks, native, JIT and NativeAOT, medians of five) ran four times, each in a 40 GB scope, one after another: (the runs' `metadata.json` names `8ff348b` and `fd861fd` by their earlier hashes `c820883` and `a4add89`: the commits were reworded to carry the plan IDs, and their trees differ only in the changelog's plan IDs)

1. `8ff348b` (`artifacts/bench/after` in this worktree): every output matched native and no regression against `tests/perf/baseline.tsv`; geometric means against the baseline 0.970 (JIT) and 0.978 (NativeAOT).
2. The parent `a5ab005`, from a scratch worktree with its own ffmpeg builds, right after (`before`). Against it, run 1 was neutral overall (geometric mean of the per-benchmark ratio quotients 0.989 JIT, 1.003 NativeAOT), faster on huffbench (0.67 and 0.68), lame (0.77, 0.84), heapsort (0.86) and fftbench (NativeAOT 0.86), and slower on ffmpeg HEVC (1.09, 1.13), which led to the change in the previous section; mafft (1.04) and richards (JIT 1.09) also moved.
3. The `tail.` experiment (micro and suite groups) described under BE-44, against run 1.
4. The final commit `fd861fd` (`artifacts/bench/after2`), after the gate: every output matched native and no regression against the baseline (host speed 0.960 of the baseline host); geometric means against the baseline 0.988 (JIT) and 0.983 (NativeAOT). Against run 2 it is neutral overall: geometric mean of the ratio quotients 1.003 (JIT) and 0.999 (NativeAOT).

The harness runs each program five times in one session, so short programs and single runs scatter by 5–15%. The entries that moved by more than that between runs 2 and 4 were timed again with `scripts/ab-time.py 11`, alternating the two runs' own builds (median seconds; ffmpeg from the A/B above):

| Benchmark | native | before NativeAOT | after NativeAOT | before JIT | after JIT |
| --- | ---: | ---: | ---: | ---: | ---: |
| huffbench | 4.396 | 8.246 | 5.665 (0.69) | 8.415 | 5.605 (0.67) |
| ffmpeg-hevc-1t | 2.037 | 2.396 | 2.363 (0.99) | 2.930 | 2.843 (0.97) |
| ffmpeg-h264-1t | 1.241 | 1.752 | 1.716 (0.98) | – | – |
| lame | 0.060 | 0.108 | 0.101 (0.94) | 0.327 | 0.317 (0.97) |
| ptrdist-yacr2 | 0.166 | 0.212 | 0.202 (0.95) | 0.380 | 0.394 (1.04) |
| richards | 0.337 | 0.421 | 0.432 (1.03) | 0.491 | 0.474 (0.97) |
| ptrdist-anagram | 0.264 | 0.399 | 0.443 (1.11) | 0.444 | 0.431 (0.97) |

ptrdist-anagram's NativeAOT build is the one consistent slowdown, and this change does not cause it: compiled with all three lowerings switched back to branches, its CIL is byte-identical to the parent's, and the NativeAOT executable published from it takes 0.443 s too. `LlvmNet.Runtime` has no source changes, but each worktree builds a different `LlvmNet.Runtime.dll` (by path), and NativeAOT compiles the runtime with the program, so the same program gets another code layout; this tight loop moves by 10% with it. susan, jpeg, float80 and startup run for 3–16 ms and scatter by more than their differences.

## Gates

`tests/run.sh --tag core --tag toolchain --exclude-tag slow` (8 jobs, 24 GB scope):

- `fd861fd`: 96 passed, 0 failed in 476 s.
- `8ff348b`: 96 passed, 0 failed in 638 s, with load average up to 65 from other sessions. The run before the 16-byte change (95 passed) failed only `support-matrix`, which regenerated the page because the `indirectbr` probe's diagnostic names the other `blockaddress` now (a branch-free select evaluates both operands); the page is regenerated in the commit.
