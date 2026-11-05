# Benchmarks

`scripts/benchmark.py` measures llvmnet's portable mode against native builds of the same sources and fails when a benchmark gets slower than its baseline (PF-01). The nightly CI job runs it; results recorded for the plan go to `docs/evidence/`. There is no numeric performance target for 1.0 ([ADR-08](../plan/decisions.md#adr-08-performance-target)): the harness publishes numbers and guards against regressions. [video-benchmark.md](video-benchmark.md) describes the separate whole-file HEVC comparison of the SIMD128 profile.

## The benchmark set

`tests/perf/benchmarks.tsv` lists every benchmark with its group, sources, flags, arguments and how its output is checked:

- **micro**: the programs in `tests/perf/`, each one cost llvmnet translates in its own way: `malloc`, `fwrite` (stdio buffering), `printf`, `atomics`, `mutex` (pthread mutexes and condition variables under contention), `calli` (indirect calls), `varargs`, `float80` (`long double`, computed in software), `memcpy`, `alloca` (variable-length arrays), `callback` (taking function addresses).
- **startup**: `hello.c`, whose time is process startup; reported, but left out of the geometric means.
- **simd**: the SIMD survey's kernels (`tests/perf/simd-kernels.c`: `saxpy`, `i32mix`, `sad8`, `dot16`, `max8`, `fdot8`, with `-ffp-contract=off`) at each ISA level: `simd-<kernel>` at the baseline, `-v2` and `-v3` with `--isa=x86-64-v2` and `x86-64-v3`, which the native builds get as `-march` (SV-20). Only the native and the vectorized engines build them, so the group runs with `--vectorized`; it is left out of the set's geometric means, and the summary gives each level's mean ratio to vectorized native instead.
- **suite**: free programs from `llvm-test-suite` (`scripts/fetch-sources.sh` pins it): SingleSource benchmarks, Olden, Ptrdist, MiBench, tramp3d-v4, Bullet, mafft and nbench.
- **ffmpeg**: the decode harness of the ffmpeg integration build (`tests/ffmpeg/decode.c`) on two clips the harness generates with the system ffmpeg from its own test pattern (1080p HEVC 10-bit and H.264, 240 frames), with one and four decoder threads. Native is the system-mode bitcode compiled by `clang-22`, as `scripts/build-ffmpeg.sh` builds it.

Both compilers get `-O2 -fno-vectorize -fno-slp-vectorize` for the scalar columns: `-O2` vectorizes in both since SV-05, so turning the vectorizers off on both sides makes the ratio measure the translation rather than the vectorizer; `--vectorized` adds the columns with them on. Native programs link against glibc.

`--vectorized` adds three engines that build the same sources with `-O2 -fvectorize -fslp-vectorize` on both compilers (SV-05): `native-vec`, whose ratio is to the scalar native build, so it is native's own vectorization gain, and `jit-vec` and `aot-vec`, whose ratios are to `native-vec`. `--vector-flags` replaces those flags for both compilers, to compare vectorizer settings. The ffmpeg entries have only the scalar engines (their profile is built by `scripts/build-ffmpeg.sh`), and the vectorized engines have no baseline: they are reported, not judged.

## Method

1. **Build.** Every benchmark is built natively with `clang-22` or `clang++-22`, with llvmnet in portable mode (run by `dotnet`, the JIT engine) and as a NativeAOT executable published from the same linked bitcode (`--keep-ir`, then `--nativeaot`), in parallel.
2. **Validate.** Each engine runs once; its output must equal the native output (`exact`), match within a tolerance (`numeric:<tolerance>`, through `LlvmNet.Checks compare-numeric`), or, for a program whose output depends on glibc behavior musl does not share, equal a native build linked with musl (`musl`, through `scripts/musl-native-cc.sh`). Programs that print timings are checked by exit status only (`status`). This run also warms the file cache. A JIT engine then runs once more with the JIT's summary of what it compiled (`DOTNET_JitDisasmSummary`, on standard output: written to a file with `DOTNET_JitStdOutFile`, it made jpeg crash in 8 of 50 runs, the runtime's background compilations writing it too), and the harness lists the translated methods it compiled with MinOpts, which RyuJIT does to a method past one of its size limits ([13-jit-patterns.md](../plan/13-jit-patterns.md)), in `minopts.tsv` and counts them per benchmark in `results.tsv`.
3. **Measure.** Each engine runs `--repeats` times (default 5), one process at a time, alternating the engine order between repeats. A run's elapsed time includes process startup and, under the JIT, compilation; user and system time come from `wait4`, and peak RSS from GNU time (`/usr/bin/time -f %M`), which starts the program from a small process: a child's own `ru_maxrss` also counts the address space it was forked from, the harness's own 27 MB. The summary reports medians.
4. **Compare.** `tests/perf/baseline.tsv` holds each benchmark's median and fastest seconds per engine on the reference host. The harness judges the fastest of each benchmark's runs, since noise only adds time and some JIT runs are bimodal (tier-1 compilation finishing before or after a hot loop): it divides every llvmnet time by this host's speed relative to the reference host (the geometric mean of native times over baseline native times) and compares the quotient with the baseline: above 1.25 the benchmark regresses, and above 1.10 for the geometric mean of an engine's quotients the engine regresses. Regressed benchmarks are measured a second time, and only a regression that reproduces fails the run. A JIT engine also fails when a benchmark has more methods compiled with MinOpts than the baseline's `minopts` column records (JP-09); that count does not depend on the host's speed. The thresholds are the ones [the owner accepted](../plan/decisions.md#decided-by-the-owner-2026-11-01-m8) on 2026-11-05.

## Running

```sh
scripts/benchmark.py                                # everything: build, validate, measure, compare (about 40 minutes)
scripts/benchmark.py --group micro --repeats 3      # one group
scripts/benchmark.py --only 'olden-*,fannkuch'      # selected benchmarks
scripts/benchmark.py --engines native,jit           # skip the NativeAOT builds
scripts/benchmark.py --vectorized                   # also the vectorized engines (SV-05)
scripts/benchmark.py --aot-instruction-set x86-64-v2  # the NativeAOT engines at another instruction set
scripts/benchmark.py --validate-only                # build and check outputs, measure nothing
scripts/benchmark.py --update-baseline              # write this run's medians to tests/perf/baseline.tsv
scripts/benchmark.py --compare artifacts/bench/<run>/results.tsv   # apply the thresholds to an earlier run
```

The ffmpeg entries build the dependencies and both ffmpeg profiles incrementally (`scripts/build-dependencies.sh`, `scripts/build-ffmpeg.sh`) unless `--no-ffmpeg-build` is given, and need the `ffmpeg` command with libx264 and libx265 to generate the clips. Run on an otherwise idle machine. Each run writes `artifacts/bench/<UTC time>/` (or `--output`): `results.tsv` (every benchmark and engine: runs, median, minimum, maximum, user and system seconds, peak RSS, ratio to native), `summary.md` (the table as recorded in `docs/evidence/`), `metadata.json` (host, CPU, .NET and Clang versions, commit), and the builds and logs.

`tests/benchmarks.sh` (core tag) checks on every pull request that the microbenchmarks build and match native output under the JIT, and that the thresholds pass an unchanged run on a faster or slower host and fail a slower benchmark or a slower geometric mean.

## Changing the baseline

The baseline changes only in a commit that says which change moved which numbers: a performance item that made llvmnet faster, a new benchmark, a new reference host. Never update it to make a regression pass. When the reference host changes (for example to the CI runner), run `scripts/benchmark.py --update-baseline` there and commit the file together with a dated evidence entry.
