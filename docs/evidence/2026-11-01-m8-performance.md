# Evidence: M8 performance (2026-11-01)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads, `powersave` governor with boost), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang/Flang 22.1.8. Every number below was produced by a command in this repository, named with it. One section per M8 item, in the order the items were closed. There is no numeric target ([ADR-08](../plan/decisions.md#adr-08-performance-target)); the numbers are published and guarded against regressions.

## PF-01: benchmark harness and baselines

`scripts/benchmark.py` builds every benchmark of `tests/perf/benchmarks.tsv` three ways from the same sources and flags (`-O2 -fno-vectorize -fno-slp-vectorize` on both sides): natively with `clang-22` against glibc, with llvmnet in portable mode run by `dotnet` (JIT), and as a NativeAOT executable published from the same linked bitcode. A first run of every engine must reproduce the native output (or, for `ptrdist-ft`, which uses glibc's `rand`, a native musl build of the same sources); then each engine runs five times, one process at a time, in alternating order, and the median elapsed time per process is reported with user and system time and peak RSS. [dev/benchmarks.md](../dev/benchmarks.md) describes the method; the set, the thresholds and the baseline policy are [proposed for the owner](../plan/decisions.md#proposed-for-the-owner-2026-11-01-m8).

The set: 11 microbenchmarks written for this item (`tests/perf/`: malloc, fwrite, printf, atomics, mutex, calli, varargs, Float80, memcpy, alloca, callback) and a startup probe; 42 programs from `llvm-test-suite` 22.1.8 (Dhrystone, Linpack, the Misc, CoyoteBench, BenchmarkGame, Shootout and McGill kernels, three C++ abstraction benchmarks, Olden, Ptrdist, tramp3d-v4, Bullet, MiBench jpeg, lame, sha and susan, mafft, nbench); and the ffmpeg decode harness on two clips generated from ffmpeg's test pattern (1080p HEVC 10-bit and H.264, 240 frames each, one and four threads). Three candidates are left out with a reason: PAQ8p calls `fork`, sim has a prototype that contradicts its definition (rejected by design), MiBench gsm does not build natively against glibc's headers.

Building the set found a host-layer bug, fixed in its own commit (`76ec86b`): `lseek` on a standard input redirected from a file failed with `ESPIPE`, so `rewind(stdin)` did nothing and mafft's `pairlocalalign` read no sequences (2 lines of output instead of 1,449,796).

`tests/benchmarks.sh` (core tag) builds the microbenchmarks in both forms on every pull request, checks their output under the JIT, and checks the thresholds on synthetic results: an unchanged run, a run within tolerance and runs on hosts two and three times slower or faster pass; one benchmark 40% slower (also on a slower host) and every benchmark 15% slower fail. The nightly job in `.github/workflows/ci.yml` ends with `python3 scripts/benchmark.py --output artifacts/bench/nightly`, uploads `results.tsv`, `summary.md` and `metadata.json`, and fails on a confirmed regression. **Pending:** the first GitHub run of the nightly job, which needs the repository on GitHub (FD-19); until then the job was run locally as below.

Baseline run (`python3 scripts/benchmark.py --update-baseline --jobs 12 --output artifacts/bench/pf01-baseline`, on commit `7ca67bb` plus the harness and the `lseek` fix, the machine otherwise idle apart from the desktop): 58 benchmarks built in 252 s, every output matched, 31 minutes in all. Its medians are `tests/perf/baseline.tsv`. Ratios are llvmnet time over native time per process, so the JIT column includes startup and compilation; the peak RSS columns are the largest of the five runs, and in this run they count the harness's own 27 MB as a floor (corrected under PF-11).

| Benchmark | Group | Native (s) | JIT (s) | JIT / native | NativeAOT (s) | NativeAOT / native | JIT peak RSS (MB) | Native peak RSS (MB) |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| malloc | micro | 1.283 | 0.991 | 0.77 | 0.916 | 0.71 | 53 | 27 |
| fwrite | micro | 0.430 | 1.027 | 2.39 | 0.906 | 2.11 | 50 | 27 |
| printf | micro | 0.880 | 5.681 | 6.45 | 6.235 | 7.08 | 68 | 27 |
| atomics | micro | 0.261 | 0.507 | 1.94 | 0.383 | 1.47 | 51 | 27 |
| mutex | micro | 0.881 | 2.057 | 2.34 | 1.848 | 2.10 | 50 | 27 |
| calli | micro | 1.537 | 2.297 | 1.50 | 2.103 | 1.37 | 49 | 27 |
| varargs | micro | 0.348 | 1.580 | 4.54 | 1.571 | 4.52 | 50 | 27 |
| float80 | micro | 0.006 | 1.717 | 267.60 | 1.487 | 231.77 | 66 | 27 |
| memcpy | micro | 0.284 | 0.605 | 2.13 | 0.455 | 1.60 | 49 | 27 |
| alloca | micro | 0.200 | 0.371 | 1.86 | 0.214 | 1.07 | 49 | 27 |
| callback | micro | 0.334 | 0.658 | 1.97 | 0.759 | 2.27 | 49 | 27 |
| startup | startup | 0.002 | 0.052 | 22.59 | 0.002 | 1.01 | 38 | 27 |
| dhrystone | suite | 0.089 | 0.162 | 1.82 | 0.148 | 1.67 | 39 | 27 |
| linpack | suite | 0.923 | 1.015 | 1.10 | 0.955 | 1.03 | 52 | 27 |
| richards | suite | 0.367 | 0.541 | 1.48 | 0.608 | 1.66 | 49 | 27 |
| oourafft | suite | 1.058 | 1.330 | 1.26 | 1.208 | 1.14 | 67 | 27 |
| ffbench | suite | 0.538 | 0.577 | 1.07 | 0.500 | 0.93 | 50 | 27 |
| whetstone | suite | 0.451 | 0.781 | 1.73 | 0.513 | 1.14 | 51 | 27 |
| salsa20 | suite | 2.013 | 3.399 | 1.69 | 16.882 | 8.39 | 49 | 27 |
| perlin | suite | 1.019 | 1.052 | 1.03 | 1.057 | 1.04 | 50 | 27 |
| reed-solomon | suite | 1.519 | 1.851 | 1.22 | 1.716 | 1.13 | 40 | 27 |
| almabench | suite | 2.394 | 2.516 | 1.05 | 2.968 | 1.24 | 50 | 27 |
| fftbench | suite | 1.225 | 1.251 | 1.02 | 0.887 | 0.72 | 126 | 84 |
| huffbench | suite | 4.624 | 8.755 | 1.89 | 8.899 | 1.93 | 59 | 27 |
| lpbench | suite | 0.942 | 1.279 | 1.36 | 1.272 | 1.35 | 72 | 32 |
| fannkuch | suite | 1.399 | 1.538 | 1.10 | 1.443 | 1.03 | 49 | 27 |
| recursive | suite | 0.374 | 0.890 | 2.38 | 0.461 | 1.23 | 50 | 27 |
| heapsort | suite | 1.711 | 1.524 | 0.89 | 1.417 | 0.83 | 112 | 63 |
| hash | suite | 1.297 | 2.942 | 2.27 | 3.040 | 2.34 | 258 | 216 |
| lists | suite | 1.623 | 3.158 | 1.95 | 3.190 | 1.97 | 67 | 27 |
| random | suite | 0.986 | 1.073 | 1.09 | 1.026 | 1.04 | 50 | 27 |
| queens | suite | 1.125 | 1.297 | 1.15 | 1.029 | 0.92 | 49 | 27 |
| hash2-cxx | suite | 0.682 | 1.205 | 1.77 | 0.974 | 1.43 | 53 | 27 |
| stepanov-v1p2 | suite | 2.482 | 2.597 | 1.05 | 2.488 | 1.00 | 51 | 27 |
| stepanov-abstraction | suite | 1.466 | 2.104 | 1.44 | 1.469 | 1.00 | 43 | 27 |
| functionobjects | suite | 1.817 | 3.110 | 1.71 | 2.351 | 1.29 | 41 | 27 |
| olden-bh | suite | 0.401 | 1.103 | 2.75 | 0.673 | 1.68 | 58 | 27 |
| olden-health | suite | 0.083 | 0.298 | 3.60 | 0.172 | 2.09 | 184 | 68 |
| olden-treeadd | suite | 0.054 | 0.245 | 4.56 | 0.093 | 1.73 | 310 | 130 |
| olden-tsp | suite | 0.282 | 0.763 | 2.70 | 0.525 | 1.86 | 180 | 66 |
| olden-em3d | suite | 0.936 | 1.594 | 1.70 | 0.972 | 1.04 | 123 | 49 |
| olden-voronoi | suite | 0.091 | 1.377 | 15.13 | 0.747 | 8.21 | 242 | 104 |
| ptrdist-ks | suite | 0.208 | 0.332 | 1.59 | 0.264 | 1.27 | 51 | 27 |
| ptrdist-yacr2 | suite | 0.175 | 0.533 | 3.04 | 0.245 | 1.40 | 56 | 27 |
| ptrdist-ft | suite | 0.256 | 0.538 | 2.10 | 0.389 | 1.52 | 63 | 27 |
| ptrdist-anagram | suite | 0.268 | 0.484 | 1.81 | 0.479 | 1.79 | 52 | 27 |
| tramp3d | suite | 0.072 | 0.786 | 10.84 | 0.108 | 1.49 | 70 | 27 |
| bullet | suite | 1.357 | 3.064 | 2.26 | 1.857 | 1.37 | 116 | 58 |
| jpeg | suite | 0.002 | 0.097 | 41.39 | 0.004 | 1.87 | 50 | 27 |
| lame | suite | 0.052 | 0.410 | 7.93 | 0.097 | 1.88 | 53 | 27 |
| sha | suite | 0.011 | 0.076 | 7.28 | 0.021 | 1.99 | 50 | 27 |
| susan | suite | 0.013 | 0.091 | 7.22 | 0.017 | 1.34 | 50 | 27 |
| mafft | suite | 8.002 | 17.376 | 2.17 | 16.907 | 2.11 | 224 | 147 |
| nbench | suite | 0.506 | 1.088 | 2.15 | 0.738 | 1.46 | 51 | 27 |
| ffmpeg-hevc-1t | ffmpeg | 2.216 | 3.158 | 1.43 | 2.666 | 1.20 | 150 | 54 |
| ffmpeg-hevc-4t | ffmpeg | 0.786 | 1.574 | 2.00 | 0.981 | 1.25 | 158 | 87 |
| ffmpeg-h264-1t | ffmpeg | 1.295 | 4.145 | 3.20 | 2.891 | 2.23 | 150 | 29 |
| ffmpeg-h264-4t | ffmpeg | 0.410 | 1.565 | 3.82 | 0.872 | 2.13 | 150 | 50 |

Geometric means of the ratios (startup excluded): JIT 2.42 and NativeAOT 1.72 over 57 benchmarks; by group, microbenchmarks 3.43 and 3.01, `llvm-test-suite` 2.21 and 1.49, ffmpeg 2.43 and 1.63. What stands out, and which item takes it up:

- **Startup and compilation** dominate the programs that run for less than about 0.1 s natively (jpeg, sha, susan, lame, tramp3d, the small Olden inputs): 7 to 41 times native under the JIT, 1.3 to 2.0 under NativeAOT (PF-06, PF-10). A hello-world process takes 52 ms under the JIT and 2 ms as NativeAOT or native.
- **`long double`** is computed in software: 268 times native for the Float80 microbenchmark. printf is 6.5 times native; musl formats `%f` with `long double` arithmetic, and PF-03 measures how much of the gap that is.
- **Variadic calls** pack a software `va_list` record: 4.5 times native (structural; the record layout is frozen in `dotnet64-v2`).
- **salsa20 as NativeAOT** is 8.4 times native while the JIT runs it at 1.7: its rotates become calls to a width-generic funnel-shift helper, whose division by the width only disappears when the helper is inlined; PF-06 looks at the NativeAOT code.
- **olden-voronoi** is 15 times native under the JIT and 8 as NativeAOT (PF-04, PF-06 investigate).
- **Threads**: the four-thread ffmpeg decodes scale worse than native (PF-02).

## PF-02: atomics and futex-based mutexes on the threaded ffmpeg case

BE-16 made aligned atomics lock-free on 2026-10-30 (`d280b0d`), measured then only on a contention fixture; musl's mutexes and condition variables, which wait in `futex`, had been tested only functionally. The question here was whether either serializes the four-thread decode.

**Where the four-thread time goes.** From the PF-01 baseline, the speedup from one to four decoder threads:

| Clip | Native | JIT | NativeAOT |
| --- | ---: | ---: | ---: |
| HEVC 1080p 10-bit, 240 frames | 2.82 (2.216 s to 0.786 s) | 2.01 (3.158 s to 1.574 s) | 2.72 (2.666 s to 0.981 s) |
| H.264 1080p, 240 frames | 3.16 (1.295 s to 0.410 s) | 2.65 (4.145 s to 1.565 s) | 3.32 (2.891 s to 0.872 s) |

NativeAOT, which runs the same translated code without a JIT, scales as native does; the JIT engine's shortfall matches a serial part of about half a second, the process start and the compilation of the decoder before the threads run compiled code (3.158 s = 0.5 + 2.66; at four threads 0.5 + 2.66 / 2.72 = 1.48 s, measured 1.57 s). A CPU-sampling profile of the four-thread HEVC decode under the JIT (`dotnet-trace collect --profile dotnet-sampled-thread-time`, 8.3 s of thread time) agrees: threads spent 1.93 s blocked in `futex` waits, the frame threads waiting for each other's progress as they do natively, but only 0.11 s of CPU (2.3% of 4.7 s) went to the futex code and monitor contention. (The same profile attributed 28% of the CPU to zeroing stack buffers; PF-04 showed that to be an artifact of the sampler for this clip, and the timings below, not the profile, carry the conclusion.)

**What the microbenchmarks showed.** The contended paths are slower than native where they are exercised directly: the `atomics` microbenchmark ran at 1.94 times native under the JIT, `mutex` (four threads on one mutex, then a condition-variable ping-pong) at 2.34. Profiles of `mutex` showed two causes:

- Every atomic operation called a runtime helper (`Atomics.Load`, `CompareExchange`, `Modify`) that tests the alignment and switches on the width before reaching `Interlocked`. Aligned 32- and 64-bit atomics (musl's `a_cas`, `a_swap`, `a_fetch_add`, C11 atomics) are now emitted inline as `Volatile.Read` and `Interlocked.Exchange`, `CompareExchange`, `Add`, `And` and `Or`, the instructions the helper's aligned path ends in; under-aligned atomics, 8- and 16-bit widths and the other operations keep the helper, which locks misaligned locations as before.
- `futex` kept one monitor per address in a `ConcurrentDictionary` that never shrank, woke with `Monitor.Pulse` (a woken thread then competed for the monitor), returned the requested rather than the actual wake count and implemented requeue as a wake of every waiter. In the contended mutex 96% of CPU samples were `Monitor.Enter_Slowpath` spinning on those monitors. Futexes are now kept as the kernel keeps them: 256 buckets hashed by address, each a short spin-locked queue of waiters; a waiter blocks on its own event, a wake takes waiters off the queue under the bucket lock and signals them after releasing it, returns the number woken, and `FUTEX_REQUEUE`/`FUTEX_CMP_REQUEUE` move waiters to the second address. No state per address outlives a wait. The priority-inheritance operations keep their owner-tracking monitors. `tests/LlvmNet.UnitTests/HostFutexTests.cs` checks the value test, timeouts, wake counts, bitsets and requeue.

**Before and after** (`python3 scripts/benchmark.py --only 'mutex,atomics,ffmpeg-*' --output artifacts/bench/pf02-after`, medians of five, compared with the PF-01 baseline; host speed 0.96 of the baseline run):

| Benchmark | Native (s) | JIT before | JIT after | NativeAOT before | NativeAOT after |
| --- | ---: | ---: | ---: | ---: | ---: |
| atomics | 0.260 | 0.507 | 0.311 | 0.383 | 0.259 |
| mutex | 0.834 | 2.057 | 1.146 | 1.848 | 1.083 |
| ffmpeg-hevc-1t | 2.077 | 3.158 | 3.144 | 2.666 | 2.564 |
| ffmpeg-hevc-4t | 0.759 | 1.574 | 1.516 | 0.981 | 0.979 |
| ffmpeg-h264-1t | 1.253 | 4.145 | 4.084 | 2.891 | 2.878 |
| ffmpeg-h264-4t | 0.389 | 1.565 | 1.565 | 0.872 | 0.811 |

atomics goes from 1.94 to 1.20 times native under the JIT and to native speed as NativeAOT; mutex from 2.34 to 1.37 and from 2.10 to 1.30. The ffmpeg decodes stay within the run-to-run noise (host-normalized 0.97 to 1.04 of the baseline), as the profile predicted: the four-thread case was not serialized by atomics or locks. Separated by an A/B run (twelve alternating runs each, medians): the old `mutex.dll` with the old runtime 1.79 s, the same `mutex.dll` with the new runtime (futex only) 1.33 s, recompiled with inline atomics 1.16 s, native 1.00 s in that run.

Gates: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 92 passed, 0 failed in 503 s; `dotnet test tests/LlvmNet.UnitTests -c Release` 113 passed (five new futex tests, run three times); `tests/run.sh atomics threads host thread-stack thread-local portable-openmp fortran-concurrent lifecycle` passed before the gate; format and shellcheck clean.

**Follow-up fix (2026-11-01).** musl's libc-test suite (`tests/run.sh libc-conformance`, nightly) then found two problems the core gate does not exercise, and the futex code was fixed in its own commit: a dead thread's robust priority-inheritance mutex is released by musl with a plain `FUTEX_WAKE`, which no longer reached the `FUTEX_LOCK_PI` waiters (`functional/pthread_robust` hung), and `pthread_cancel`, which interrupts the target thread, could interrupt the bucket lock's fallback to `Thread.Sleep(0)` inside a wake and abandon it (`functional/pthread_cancel-points` and `regression/pthread_cancel-sem_wait` hung). The wake now also pulses priority-inheritance waiters, and the bucket lock and the wait for a wake's signal spin and yield without sleeping. After the fix libc-test fails exactly the tests it failed before M8 (functional 60 of 67, regression 59 of 67, math 192 of 195, api 74 of 74).

## PF-13: register callbacks once

Every evaluation of a function's address emitted `ldftn` twice and a call to `SystemAbi.RegisterCallback` (or `RegisterVariadicCallback`), a `ConcurrentDictionary.GetOrAdd`, in both modes. The registration maps the pointer to its method for the runtime (`SystemAbi.ResolveCallback`: `atexit` handlers, `pthread_key` destructors, the C++ terminate handler, and calls through a variadic pointer type to a function that is not variadic); in portable mode the pointer is the method itself, so only the mapping, not the value, needed it. The compiler now records every function whose address the module takes and the module initializer registers each one once, before any translated code runs; the address itself is a single `ldftn` (in system mode, of the native-callable stub, as before). Registering in the initializer keeps every lookup the runtime does working in both modes, which dropping registration in portable mode would not have for the legacy variadic-pointer calls (`tests/legacy-callbacks.sh`).

The `callback` microbenchmark stores one of two function addresses into a table and calls through it 150 million times. A/B on the same host (ten alternating runs, medians; the before binaries are the PF-01 baseline builds, run with the current runtime):

| Engine | Before (s) | After (s) | Native (s) |
| --- | ---: | ---: | ---: |
| JIT | 0.694 | 0.449 | 0.350 |
| NativeAOT | 0.781 | 0.354 | 0.350 |

`python3 scripts/benchmark.py --only 'callback,startup,calli,tramp3d,bullet,functionobjects,ffmpeg-hevc-1t,ffmpeg-h264-1t' --output artifacts/bench/pf13-after` against the baseline (host-normalized): callback 0.64 (JIT) and 0.40 (NativeAOT), the others 0.92 to 1.04, within noise; startup 48 ms against 52 ms, so registering every address-taken function of a program up front costs nothing measurable (musl and libc++ included). Gates: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 92 passed, 0 failed in 501 s; unit tests 113 passed; `tests/run.sh legacy-callbacks varargs-callbacks indirect-calls aggregate-callbacks lifecycle exit-order cpp-terminate system-abi` passed before the gate.

## PF-03: stdio buffers and the cost of a host call

musl already buffers stdio in portable mode; the per-call host writes the item names belonged to the hand-written C library that LC-17 deleted. What remained to measure was the cost of a host call and musl's buffer size, and the printf gap of PF-01.

**Host-call cost.** A probe (2 million calls each, timed inside the program with `clock_gettime`; native glibc, native musl, llvmnet JIT and NativeAOT; the regular file is in the session's scratch directory):

| Call | glibc | musl | JIT | NativeAOT |
| --- | ---: | ---: | ---: | ---: |
| `syscall(SYS_getpid)` | 41 ns | 37 ns | 63 ns | 13 ns |
| `write` of 1 byte to `/dev/null` | 58 ns | 57 ns | 61 to 83 ns | 20 ns |
| `write` of 1 byte to a regular file | 143 ns | | 237 ns | 174 ns |
| `write` of 4,096 bytes to a regular file | 635 ns | | 695 ns | 717 ns |
| `fputc` to a regular file, per character | 13.2 ns | | 17.4 ns | 13.5 ns |

A host call costs 30 to 90 ns more than the kernel's own system call under the JIT and less than that as NativeAOT (where the host answers `getpid` and `/dev/null` without entering the kernel at all); for buffered output it is a few percent.

**Buffer size.** The `fwrite` microbenchmark (1 GiB in 256-byte calls, then read back) with buffers given to `setvbuf` (musl ignores a size without a buffer):

| Buffer | glibc | musl | JIT | NativeAOT |
| --- | ---: | ---: | ---: | ---: |
| default (glibc 4,096 from `st_blksize`, musl 1,024) | 0.456 s | 0.581 s | 1.002 s | 0.837 s |
| 4,096 bytes | 0.402 s | 0.432 s | 0.742 s | 0.568 s |
| 65,536 bytes | 0.408 s | 0.398 s | 0.605 s | 0.468 s |

musl's 1 KB buffer makes four times the write calls glibc makes, and each costs more here. The port now gives streams a 4,096-byte buffer (`ports/musl/src/stdio/llvmnet/__fdopen.c`, `stdout.c`, `stdin.c`), the size glibc takes from `st_blksize` on Linux file systems: a fully buffered stream is flushed where a native glibc build flushes it, so output that interleaves buffered standard output with unbuffered standard error in one file comes out in the same order as natively. Larger buffers would gain another 18%, but would also move those flush points away from native builds and cost memory per open stream; `BUFSIZ`, part of the frozen `dotnet64-v2` ABI, stays 1,024, so `setbuf` is unchanged.

**printf.** A profile of the printf microbenchmark (`%d %s %.3f %08x %ld`, 5 million lines) pointed at `Float80.Compare`: musl formats floating point with `long double` arithmetic, and the comparison scaled both operands to a common exponent as `BigInteger` values, thousands of bits wide when one operand is zero. It now compares sign, normalized binary exponent and significand directly (exact for zeros of both signs, denormals, unnormals and infinities, checked against the `BigInteger` order over all pairs of 310 encodings in `Float80Tests.ComparisonsFollowTheExactValues`). The same profile blamed most of the remaining time on zeroing `printf_core`'s 7 KB stack buffer; PF-04 removed the zeroing and found that share overstated by the profiler (printf gained 8%), so what remains is the formatting itself, `long double` arithmetic in software included.

**Before and after** (`python3 scripts/benchmark.py --only 'printf,fwrite,float80,varargs,mafft,lame,ptrdist-yacr2,olden-voronoi,almabench' --output artifacts/bench/pf03-after`, against the PF-01 baseline):

| Benchmark | Native (s) | JIT before | JIT after | NativeAOT before | NativeAOT after |
| --- | ---: | ---: | ---: | ---: | ---: |
| fwrite | 0.412 | 1.027 | 0.756 | 0.906 | 0.580 |
| printf | 0.888 | 5.681 | 4.643 | 6.235 | 5.179 |
| float80 | 0.006 | 1.717 | 1.518 | 1.487 | 1.365 |
| olden-voronoi | 0.080 | 1.377 | 0.986 | 0.747 | 0.641 |
| mafft (47 MB of output) | 8.021 | 17.376 | 16.467 | 16.907 | 16.529 |

The other benchmarks of that run (varargs, lame, ptrdist-yacr2, almabench) are within 0.95 to 1.07 of the baseline. Gates: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 92 passed, 0 failed in 503 s (with the rebuilt sysroot: 1,347 of 1,347 musl sources); unit tests 114 passed.

## PF-04: allocas without zeroing, and an arena for scoped allocas

Two costs of allocas, each measured with A/B runs rather than profiles (see the note on sampling below):

- **Zeroing.** Every emitted method carried the `localsinit` flag, which `MethodBuilder` sets by default, so the JIT and NativeAOT zeroed each method's locals and every `localloc` block on entry. An alloca's contents are undefined in LLVM, so the zeroing is pure cost, and C code keeps large buffers on the stack: musl's `printf_core` 7 KB for `long double` formatting, ffmpeg's H.264 motion compensation and intra prediction their scratch blocks, a variadic call its `va_list` record. Translated functions are now emitted without it (C# calls this `SkipLocalsInit`); the one local whose first value the emitter relied on, the setjmp dispatch index, is initialized explicitly. The runtime's own C# and the host-call wrappers keep the flag.
- **Scoped allocas** (functions with C variable-length arrays, which Clang brackets with `llvm.stacksave` and `llvm.stackrestore`, and functions that call `setjmp`) cannot use `localloc`, whose memory lasts until the method returns. Each call of such a function allocated a `StackMemory` object, a list and one `NativeMemory.Alloc` per alloca, freed in a `finally`. `StackMemory` is now a per-thread bump arena (BE-17's arena): the function saves the arena top on entry and restores it on every exit, `stacksave`, `stackrestore` and `longjmp` save and restore tops, and 64 KB chunks (larger for large requests) are added and released as the top moves, with one spare kept so that a loop around a chunk boundary does not allocate; a thread's chunks are freed when it exits.

**Before and after.** A/B runs, alternating, medians: ffmpeg's `decode.dll` from the PF-01 baseline build (compiled before PF-02's inline atomics and PF-13, which left ffmpeg's times unchanged) against the rebuilt one, both with the current runtime; the alloca microbenchmark from the baseline build with its runtime against this build; deepsjeng from the SPEC kit on this host, built by the toolchain at `03c5dda` (PF-03) and by this one with the harness's flags:

| Program | Native | JIT before | JIT after | NativeAOT before | NativeAOT after |
| --- | ---: | ---: | ---: | ---: | ---: |
| ffmpeg H.264 1080p, one thread (6 runs) | | 4.204 s | 2.974 s | | |
| ffmpeg HEVC 1080p 10-bit, one thread (6 runs) | | 3.261 s | 3.227 s | | |
| alloca microbenchmark (10 runs) | 0.202 s | 0.374 s | 0.358 s | 0.209 s | 0.191 s |
| SPEC 531.deepsjeng_r, test input (3 runs) | 4.120 s | 5.180 s | 5.043 s | 5.289 s | 5.030 s |

and the harness against the PF-01 baseline (`python3 scripts/benchmark.py --only 'alloca,printf,varargs,olden-*,ptrdist-*,lame,jpeg,mafft,bullet,tramp3d,ffmpeg-*' --output artifacts/bench/pf04-after`; times in seconds, the baseline in parentheses):

| Benchmark | Native | JIT | NativeAOT |
| --- | ---: | ---: | ---: |
| varargs | 0.342 | 1.178 (1.580) | 0.883 (1.571) |
| printf | 0.893 | 4.284 (5.681) | 4.454 (6.235) |
| olden-em3d | 0.925 | 1.048 (1.594) | 0.967 (0.972) |
| ffmpeg-h264-1t | 1.341 | 2.951 (4.145) | 1.954 (2.891) |
| ffmpeg-h264-4t | 0.410 | 1.373 (1.565) | 0.585 (0.872) |

H.264 decoding goes from 3.20 to 2.20 times native under the JIT and from 2.23 to 1.46 as NativeAOT; the other benchmarks of the run are within the noise of the baseline (0.90 to 1.13), except olden-voronoi, faster since PF-03. printf includes PF-03's change. SPEC 500.perlbench_r, the item's other example, needs the SPEC harness's build and was not measured; deepsjeng, which keeps no large stack buffers, gains 3 to 5%.

**A correction to PF-02.** The CPU-sampling profiles above come from `dotnet-trace`'s thread-time profile, which stops threads at safe points; a method without safe points hands its samples to the next method that has one. The four-thread HEVC profile attributed 28% of the CPU to `Buffer.ZeroMemoryInternal`, and the printf profile 97%, but removing the zeroing left HEVC unchanged and took 8% off printf, and the next printf profile blamed 44% on an uncontended `Monitor.Enter` (the runtime's own `Monitor.LockContentionCount` was 0). From here on profiles only suggest what to try, and each change is judged by timing the programs; the profile shares quoted under PF-02 and PF-03 carry this bias, while their before-and-after timings do not. The PF-02 and PF-03 texts are corrected accordingly.

The gate found one test that depended on zeroed stack memory: `tests/c/musl/stdio.c` mixed positional and non-positional `printf` arguments, which is undefined (Clang warns) and makes musl read its uninitialized `nl_arg` array; native musl printed `(null)` because its fresh stack happened to hold zeros. The fixture now uses a valid format (its next line already tests positional arguments).

Gates: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 91 passed and `musl` failed as described, then passed with the corrected fixture; unit tests 114 passed; libc-test fails exactly the tests it failed before M8; `JOBS=8 FUZZ_SEEDS=200 FUZZ_GENERATORS=csmith tests/run.sh fuzz` compiled 200 csmith programs at `-O0` to `-O3` in both modes: 173 matched native in every mode, 25 were skipped because the native program did not finish in 10 s, 2 were rejected with a diagnostic as before, none differed. YARPGen was not run: one of its first 100 native programs needs more than 40 GB on its own, and the fuzz test's default of `nproc/2` parallel jobs had exhausted this host's memory (the test runs here inside memory-capped scopes since); `tests/run.sh stack nonlocal-jumps large-locals vectors atomics fortran-stack cpp exception-pointer semantics io` passed before the gate.

## PF-05: indirect calls

A translated indirect call loads the target address and executes `calli` with the call site's signature; a function's address is a `ldftn` of its method (PF-13). The alternatives were measured with a probe that dispatches 200 million calls in the `calli` microbenchmark's pattern, through a table of eight functions chosen by a pseudo-random index (`dotnet run -c Release --project tests/perf/probes/dispatch`, and published as NativeAOT; second of two rounds, nanoseconds per call):

| Dispatch | JIT | NativeAOT |
| --- | ---: | ---: |
| `calli` through a table of addresses (what llvmnet emits) | 8.04 | 6.98 |
| direct calls behind a `switch` on the index | 8.49 | 8.06 |
| delegate table | 12.78 | 8.51 |
| interface table | 14.28 | 9.10 |
| one target: `calli` | 1.73 | 1.96 |
| one target: delegate (guarded devirtualization under the JIT) | 1.41 | 1.78 |
| one target: direct call | 1.21 | 1.57 |

`calli` is the fastest polymorphic dispatch in both engines; a delegate is 0.2 to 0.3 ns faster only at a call site that always reaches one target, and would need an object per function address plus a lookup from the C pointer at every call, which costs more than it saves. Counted with `--check-indirect-calls` and a temporary counter in the runtime (not committed), the ffmpeg decodes make 9.3 million indirect calls for the 240 HEVC frames and 23.9 million for the 240 H.264 frames, mostly DSP function-table calls; at the 0.5 ns `calli` costs over a direct call that is 5 and 12 ms of 3-second decodes. Indirect calls therefore stay as they are. x264 is not in the benchmark set (proposed for later in [decisions.md](../plan/decisions.md#proposed-for-the-owner-2026-11-01-m8)).

The A/B runs in this file use `scripts/ab-time.py` (alternating runs, medians), the PF-03 tables `tests/perf/probes/hostcall.c` and `tests/perf/probes/fwrite-buffer.c`.

## PF-10: startup latency

A hello-world program (`tests/perf/hello.c`), A/B with `scripts/ab-time.py` (40 to 60 alternating runs, medians): native 1 ms, NativeAOT 2 ms, the JIT 38 ms, and an empty C# program that writes the same line on the same runtime 17 ms, which is the floor of any `dotnet program.dll`. Of the 21 ms above it, most is JIT compilation before `main` (`DOTNET_JitDisasmSummary=1`): 194 methods, 28 of them translated (musl's start-up, `puts`, `exit`) and 166 from the runtime library and the base library, among them about 35 generic LINQ methods instantiated by three expressions that run at every start (copying the environment, building the table of refused system calls, creating the futex buckets) and the `Dictionary` the table used. These are now loops and an array indexed by system-call number (also one lookup less per system call): 134 methods before `main`, and the hello-world program starts in 36 ms instead of 39; MiBench jpeg, whose 0.09 s are mostly start-up, in 86 ms instead of 88.

Measured and not adopted:

| Variant (hello world, JIT) | Median |
| --- | ---: |
| as emitted | 37 ms |
| `System.Globalization.Invariant` (no ICU) | 36 ms |
| `TieredPGO` off | 37 ms |
| tiered compilation off | 58 ms |
| `LlvmNet.Runtime.dll` compiled ReadyToRun for linux-x64 (157 KB to 370 KB) | 35 ms |
| ReadyToRun runtime and no ICU | 32 ms |

**Invariant globalization is not safe for portable images.** On the Windows host, a portable program that sets `TZ=Europe/London` printed `2024-07-01 13:00:00 +0100 isdst=1` for 12:00 UTC, and `12:00:00 +0000 isdst=0` with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`: .NET on Windows needs ICU to map IANA zone names to its own zones, so the generated zone information (LC-09) silently becomes UTC. The JIT's `runtimeconfig.json`, shared by every host, therefore keeps ICU. NativeAOT projects had set `InvariantGlobalization` unconditionally, which made exactly that happen in win-x64 executables: published on the Windows host, the same program printed `12:00:00 +0000` with the old setting and `13:00:00 +0100` with the new one, which sets it only for runtime identifiers other than `win-*` (`ToolchainTests.NativeAotProjectsKeepIcuOnWindows`; `dotnet msbuild -getProperty` gives `true` for linux-x64 and osx-arm64 and nothing for win-x64). ReadyToRun would save 3 ms on linux-x64 only and more than double the runtime library that every portable image carries; it belongs to the ReadyToRun evaluation of PF-06 (TC-08). NativeAOT executables already start as fast as native ones.

## PF-11: memory

**The harness's peak RSS was wrong for small programs.** On Linux a child's `ru_maxrss` also counts the address space it was forked from, even through `posix_spawn`: a `true` started from a Python process holding 212 MB reported 212 MB. The PF-01 baseline therefore shows 27.2 MB, the harness's own size, for every program smaller than that. `scripts/benchmark.py` now starts each measured process through GNU time and takes `%M` from it, which reports `true` at 1 MB; it also waits for the process instead of polling every 2 ms, so short runs are timed exactly. Peak RSS medians of five runs (`/usr/bin/time -f %M`):

| Program | Native | JIT | NativeAOT |
| --- | ---: | ---: | ---: |
| hello world | 1.8 MB | 36.8 MB | |
| empty C# program, same runtime | | 27.9 MB | |
| malloc microbenchmark | 11.2 MB | 51.8 MB | 18.4 MB |
| Bullet | 57.6 MB | 114.7 MB | 67.7 MB |
| ffmpeg HEVC 1080p, four threads | 86.4 MB | 158.0 MB | 98.7 MB |
| ffmpeg H.264 1080p, one thread | 28.5 MB | 175.8 MB | 39.4 MB |

NativeAOT stays within 7 to 12 MB of native, the runtime's own share; the JIT adds the runtime (28 MB for any `dotnet program.dll`) and the code it generates, which grows with the program: 9 MB for hello world, 60 to 150 MB for Bullet and ffmpeg. The 4K video comparison the item quotes (330 MB against 251 MB) used private media and was not repeated.

**GC configuration.** Translated programs keep their data in native memory (musl's `malloc` over the host's `mmap`), so the managed heap holds only the runtime's own objects, and the GC settings that matter for managed programs change nothing: on hello world and the H.264 decode, `DOTNET_gcConcurrent=0` (no background GC thread), `DOTNET_GCgen0size=0x100000` and `DOTNET_GCConserveMemory=5` each moved peak RSS by less than 1 MB and time not at all. What does move RSS is the JIT's tiering: on the H.264 decode `DOTNET_TieredPGO=0` gave 155 MB and 2.77 s against 176 MB and 2.87 s, and `DOTNET_TieredCompilation=0` 156 MB and 2.63 s (but hello world 58 ms instead of 37), which PF-06 evaluates for `runtimeconfig.json`. No GC setting is added.

**Large-page arenas for musl `malloc`** were not pursued: the malloc microbenchmark already runs faster than native glibc's under both engines (0.97 s and 0.86 s against 1.20 s), and RSS above native comes from the runtime and generated code, not from the C heap.

Gates: no compiler or runtime change; `tests/run.sh benchmarks` passed with the new process runner.

## PF-08: NativeAOT instruction sets

**The regression explained.** `tests/perf/probes/simd128` times the SIMD128 helpers of `LlvmNet.Runtime.Simd128` that ffmpeg's SIMD128 profile calls most, published as NativeAOT with each instruction set (`dotnet publish tests/perf/probes/simd128 -c Release -r linux-x64 -p:PublishAot=true -p:IlcInstructionSet=<set>`; nanoseconds per iteration, of which about 4 ns is the probe's own delegate call):

| Helper | default | `x86-64` | `x86-64-v2` | `x86-64-v3` | `x86-64-v4` | `native` | JIT |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `Swizzle` (variable shuffle) | 12.64 | 12.83 | 4.48 | 4.31 | 4.48 | 6.23 | 2.72 |
| `Shuffle` (lanes built at run time) | 21.99 | 21.90 | 4.35 | 4.37 | 4.78 | 4.42 | 5.44 |
| `ExtendLowInt8` (widening) | 4.83 | | 4.36 | 4.51 | 4.38 | 4.29 | 6.11 |
| `NarrowInt16` (saturating) | 5.08 | | 5.14 | 5.08 | 5.07 | 5.08 | 7.35 |
| `AddSaturateInt16` | 8.32 | | 6.80 | 6.45 | 6.36 | 6.36 | 8.89 |
| `MultiplyInt16`, `BitSelect`, `AddInt32` | 4.1 to 4.7 | | 4.0 to 4.7 | 3.9 to 4.7 | 3.9 to 4.5 | 3.9 to 4.5 | 5.2 to 5.7 |

The default is the NativeAOT compiler's `x86-64` level (SSE2), identical to it in every row. At that level `Vector128.Shuffle` with indices that are not compile-time constants cannot use SSSE3's `pshufb` and falls back to a software loop, although `Ssse3.IsSupported` is true when the program runs: the shuffles take three to five times as long. Widening, narrowing and the rest have SSE2 sequences and do not change. Of the plan's two hypotheses, non-constant shuffle indices is the cause; widening is not.

**Effect on ffmpeg.** The portable SIMD128 and scalar profiles of the decode harness as NativeAOT for each `--aot-instruction-set` (`bin/llvmnet --runtime=portable [--simd128] --nativeaot --aot-instruction-set <set> .../decode.bc`; 240 frames of the HEVC 1080p 10-bit clip, one thread, `scripts/ab-time.py`, medians of five; the first 48 frames' hashes equal native's for every build):

| Build | baseline | `x86-64-v3` | `native` | JIT |
| --- | ---: | ---: | ---: | ---: |
| scalar | 2.714 s | 2.628 s | 2.652 s | 3.325 s |
| SIMD128 | 3.285 s | 2.679 s | 2.602 s | 3.281 s |

Native: 2.324 s. With the baseline instruction set the SIMD128 profile is 21% slower than the scalar one (the 2.2 times of the earlier SIMD128 study measured an older runtime and build); at `x86-64-v3` the two are within 2% and both within 15% of native.

**`x86-64-v3` option.** `--aot-instruction-set x86-64-v3` selects ILC's `x86-64-v3` level (AVX2, BMI1/2, FMA, LZCNT, MOVBE). It is rejected for a target that is not x64 (`--runtime-id linux-arm64`), and in a relocatable project (`--aot-project`) it applies only when the project is published for an x64 runtime identifier (`ToolchainTests.X86V3AppliesToX64Targets`). Baseline stays the default, as the owner decided; [user/simd128.md](../user/simd128.md) documents the trade-off. Because `x86-64-v2` already provides `pshufb`, a v2 default for SIMD128 builds is [proposed for the owner](../plan/decisions.md#proposed-for-the-owner-2026-11-01-m8).

Gates: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 91 passed; `driver` failed because it expected the old error text (`requires baseline or native`), and passes with the new text and a new check that `x86-64-v3` is refused for linux-arm64; unit tests 116 passed; `tests/run.sh driver-reference` regenerated the option table of `docs/user/driver.md`.

## PF-04 follow-up: `localsinit` and on-stack replacement

The first full run after PF-04 (`python3 scripts/benchmark.py --engines native,jit --repeats 3 --output artifacts/bench/pf06-default`) failed the regression threshold on lpbench under the JIT: 3.22 s against the baseline's 1.28 s, 2.57 times host-normalized, confirmed by the second measurement. The nightly check did what it is for. Bisecting with A/B runs: lpbench built by the PF-03 toolchain took 1.21 s; the PF-03 program with the current runtime 1.19 s and the current program with the PF-03 runtime 3.08 s, so the emitted IL was the cause; its hot function's IL was unchanged in size, and setting the `localsinit` flag back on every method of the slow `lp.dll` with a binary patch restored 1.20 s. The program was also fast with `DOTNET_TieredCompilation=0` (1.28 s) or `DOTNET_TC_QuickJitForLoops=0` (1.16 s) and slower with `DOTNET_TC_OnStackReplacement=0` (4.53 s): `dgefa` is called once and spends the run in one loop nest, which the JIT replaces on the stack with optimized code, and that on-stack-replacement code runs 2.6 times slower when the method lacks `localsinit`.

Methods now drop the flag only when they use `localloc` (allocas, `byval` copies, the variadic call area): those are the methods whose zeroing PF-04 removed, and the JIT never replaces them on the stack. Measured after the change (`--output artifacts/bench/initfix`, against the PF-01 baseline, host-normalized): lpbench 0.92 (JIT) and 0.88 (NativeAOT), ffmpeg H.264 one thread 0.74 and 0.68, printf 0.74 and 0.71, varargs 0.75 and 0.57, olden-em3d 0.65, alloca 0.96 and 0.85, fannkuch 1.00; PF-04's gains are kept. ptrdist-yacr2 then crossed the threshold (1.26 under the JIT) without a cause: its JIT times are bimodal, 0.50 to 0.52 s or about 0.68 s depending on when tier-1 compilation finishes, and twelve alternating runs of the baseline build and the current one gave medians of 0.663 s and 0.673 s. Gates: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 92 passed; unit tests 116 passed.

## PF-07: over-limit methods

The JIT compiles a method with MinOpts, without register allocation or optimization, when it exceeds fixed limits (about 60 KB of IL, 2,000 basic blocks, 2,000 locals); `--diagnose-limits` (BE-25) counts such functions in a program. Which of them run hot was measured with `DOTNET_JitDisasmSummary=1` on every JIT benchmark of the set (`... then to MinOpts` entries):

| Program | Functions compiled with MinOpts |
| --- | --- |
| ffmpeg, H.264 decode | `ff_h264_decode_mb_cabac` (167,448 bytes of IL), `ff_h264_filter_mb` (79,946), `ff_h264_queue_decode_slice` (71,582), `hl_decode_mb_complex` (61,535), `ff_er_frame_end` (57,780) |
| ffmpeg, HEVC decode | none |
| tramp3d-v4 | two constructors and `main` (run once) |
| mafft | `constants` (table set-up, run once) |
| the other 51 suite programs and microbenchmarks | none |

The H.264 functions are the per-macroblock CABAC decoding, deblocking and reconstruction, so they carry much of the decode. The limits are constants of the release JIT: `DOTNET_JITMinOptsCodeSize`, `...BbCount`, `...LvNumCount`, `...LvRefCount` and `...InstrCount` set to their maximum left all five in MinOpts. NativeAOT compiles every method optimized (ahead-of-time compilation never falls back to MinOpts), so the gap between the engines bounds the penalty: H.264 runs 1.51 times as long under the JIT as NativeAOT (2.95 s against 1.95 s, PF-04's run), HEVC, without MinOpts functions, 1.19 times (3.17 s against 2.66 s). Were H.264's gap HEVC's, its JIT time would be about 2.32 s, so MinOpts costs about a fifth of it. SPEC 502.gcc_r and 500.perlbench_r, the item's targets, which BE-25 found with 40 and 23 such functions, were not run. Outlining cold regions so that hot functions stay under the limits is BE-26 (L), not started; the item stays open.

## PF-06: JIT code quality

**Funnel shifts and rotates.** PF-01 showed salsa20 at 1.7 times native under the JIT and 8.4 times as NativeAOT. Its rotates, `(a << b) | (a >> (32 - b))`, become `llvm.fshl.i32` with both halves the same value, which the backend lowered for every width to one helper, `Numeric.Funnel(left, right, count, width, direction)`; its `count % width` is a division unless the width is a constant, which it only becomes where the helper is inlined. The tiered JIT inlined it; NativeAOT, without profile data, did not, and paid a call and a division per rotate. 32- and 64-bit funnel shifts are now emitted as `BitOperations.RotateLeft`/`RotateRight` when both halves are the same value (JIT intrinsics, one `rol` or `ror`) and otherwise as calls to the small helpers of their width (`FunnelLeft32` and the others); other widths keep the general helper. The semantics probes gained rotate cases (45,310 probes, all matching native in both modes and as NativeAOT). A/B, three alternating runs, medians:

| salsa20 | Native | JIT | NativeAOT |
| --- | ---: | ---: | ---: |
| before | 1.997 s | 3.306 s | 16.041 s |
| after | | 2.224 s | 2.090 s |

**Tiering in `runtimeconfig.json`.** `scripts/benchmark.py` gained `--env NAME=VALUE` for the JIT engine's processes. 24 benchmarks (the four ffmpeg decodes, the large C++ programs, the call-, loop- and allocation-heavy kernels and start-up) ran under the JIT with the default tiering and three alternatives, after the `localsinit` fix (`--engines native,jit --repeats 3 --output artifacts/bench/pf06b-<setting>`; JIT medians in seconds, the last rows normalized by each run's native times):

| Benchmark | default (s) | TieredPGO=0 (s) | TieredCompilation=0 (s) | TC_QuickJitForLoops=0 (s) |
| --- | ---: | ---: | ---: | ---: |
| bullet | 3.072 | 2.633 | 1.981 | 2.435 |
| calli | 2.215 | 2.146 | 2.100 | 2.205 |
| ffmpeg-h264-1t | 3.093 | 2.945 | 2.625 | 2.788 |
| ffmpeg-h264-4t | 1.445 | 1.248 | 1.093 | 1.212 |
| ffmpeg-hevc-1t | 3.494 | 3.333 | 3.055 | 3.295 |
| ffmpeg-hevc-4t | 1.591 | 1.488 | 1.382 | 1.468 |
| functionobjects | 3.053 | 2.811 | 2.226 | 2.380 |
| hash | 2.125 | 2.213 | 2.059 | 2.119 |
| huffbench | 8.453 | 8.362 | 8.508 | 8.819 |
| jpeg | 0.087 | 0.081 | 0.116 | 0.102 |
| lame | 0.395 | 0.325 | 0.234 | 0.256 |
| lists | 3.098 | 3.142 | 3.301 | 3.082 |
| lpbench | 1.276 | 1.246 | 1.400 | 1.385 |
| mafft | 16.249 | 16.347 | 18.420 | 16.246 |
| olden-bh | 1.112 | 1.152 | 1.084 | 1.131 |
| olden-tsp | 0.879 | 0.852 | 0.741 | 0.834 |
| printf | 4.274 | 4.570 | 6.722 | 4.182 |
| ptrdist-anagram | 0.471 | 0.464 | 0.461 | 0.487 |
| ptrdist-ks | 0.355 | 0.371 | 0.350 | 0.358 |
| richards | 0.482 | 0.493 | 0.471 | 0.510 |
| salsa20 | 2.255 | 2.233 | 2.139 | 2.131 |
| startup | 0.035 | 0.039 | 0.057 | 0.056 |
| stepanov-abstraction | 2.048 | 1.631 | 1.489 | 1.532 |
| tramp3d | 0.745 | 0.627 | 0.614 | 0.732 |
| varargs | 1.191 | 1.179 | 1.002 | 1.168 |
| geometric mean against default (native-normalized, startup excluded) | 1.000 | 0.934 | 0.916 | 0.928 |
| peak RSS against default (geometric mean) | 1.000 | 1.001 | 1.016 | 1.000 |

Tiered compilation off is fastest on average but costs 22 ms of start-up (35 to 57 ms) and slows printf by 57% and mafft by 13%; `QuickJitForLoops` off costs the same start-up. Dynamic PGO off (`TieredPGO=0`) gains 6.6% without either cost: translated C has few virtual calls for profile-guided devirtualization, and the instrumented tier-0 code it runs first is slower (Bullet 14%, stepanov abstraction 20%, tramp3d 16%, four-thread H.264 14% faster; printf 7% and hash 4% slower; peak RSS unchanged). The emitted `runtimeconfig.json` now sets `System.Runtime.TieredPGO` to false; `DOTNET_TieredPGO=1` restores it for a run ([user/deployment.md](../user/deployment.md)). NativeAOT has no tiering and is unaffected.

**ReadyToRun** (TC-08) was measured under PF-10: compiling `LlvmNet.Runtime.dll` ReadyToRun saves 3 ms of start-up on linux-x64 and more than doubles the library (157 KB to 370 KB) that every portable image carries, and its code is ignored on other hosts. It is not adopted; compiling the translated program itself ReadyToRun would make the portable image architecture-specific, which NativeAOT already offers.

**Not done.** A systematic `DOTNET_JitDisasm` review of the hot kernels and the removal of avoidable `UInt128` and normalization masks; the item stays open for them.

Gates for PF-06: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 92 passed (`semantics` with the rotate probes: 45,310 probes matching native in all modes and as NativeAOT); unit tests 116 passed.

## Benchmark table after the M8 items, and the new baseline

`python3 scripts/benchmark.py --update-baseline --jobs 12 --output artifacts/bench/m8-final` on commit `d2a2711` (with the threshold change below), inside a 32 GB memory cap, all 58 benchmarks built in 235 s and matched native: no regression against the PF-01 baseline. Before is the PF-01 run (`7ca67bb`), after this one; ratios are medians of five runs over native's, peak RSS from GNU time (PF-11), so the RSS columns are correct here, unlike the PF-01 table's:

| Benchmark | Native (s) | JIT / native before | after | NativeAOT / native before | after | JIT peak RSS (MB) | NativeAOT peak RSS (MB) | Native peak RSS (MB) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| malloc | 1.245 | 0.77 | 0.84 | 0.71 | 0.69 | 52 | 18 | 11 |
| fwrite | 0.422 | 2.39 | 1.65 | 2.11 | 1.45 | 50 | 6 | 2 |
| printf | 0.909 | 6.45 | 5.05 | 7.08 | 5.00 | 67 | 22 | 2 |
| atomics | 0.269 | 1.94 | 1.16 | 1.47 | 1.02 | 50 | 6 | 2 |
| mutex | 0.945 | 2.34 | 1.20 | 2.10 | 1.03 | 50 | 6 | 2 |
| calli | 1.481 | 1.50 | 1.45 | 1.37 | 1.29 | 49 | 13 | 17 |
| varargs | 0.344 | 4.54 | 3.50 | 4.52 | 2.65 | 50 | 6 | 2 |
| float80 | 0.006 | 267.60 | 274.48 | 231.77 | 239.85 | 67 | 22 | 2 |
| memcpy | 0.278 | 2.13 | 2.10 | 1.60 | 1.62 | 50 | 7 | 4 |
| alloca | 0.201 | 1.86 | 1.58 | 1.07 | 0.97 | 49 | 6 | 2 |
| callback | 0.333 | 1.97 | 1.34 | 2.27 | 0.92 | 49 | 6 | 2 |
| startup | 0.001 | 22.59 | 26.63 | 1.01 | 1.68 | 37 | 5 | 2 |
| dhrystone | 0.082 | 1.82 | 1.91 | 1.67 | 1.63 | 38 | 5 | 2 |
| linpack | 0.895 | 1.10 | 1.21 | 1.03 | 1.03 | 52 | 6 | 2 |
| richards | 0.357 | 1.48 | 1.34 | 1.66 | 1.21 | 50 | 6 | 2 |
| oourafft | 1.059 | 1.26 | 1.22 | 1.14 | 1.11 | 57 | 12 | 2 |
| ffbench | 0.581 | 1.07 | 0.95 | 0.93 | 0.87 | 51 | 6 | 3 |
| whetstone | 0.449 | 1.73 | 1.48 | 1.14 | 1.10 | 51 | 6 | 2 |
| salsa20 | 1.981 | 1.69 | 1.14 | 8.39 | 1.06 | 49 | 6 | 2 |
| perlin | 0.988 | 1.03 | 1.01 | 1.04 | 1.04 | 49 | 6 | 2 |
| reed-solomon | 1.518 | 1.22 | 1.26 | 1.13 | 1.16 | 39 | 5 | 2 |
| almabench | 2.358 | 1.05 | 1.03 | 1.24 | 1.15 | 50 | 6 | 2 |
| fftbench | 1.170 | 1.02 | 0.87 | 0.72 | 0.74 | 124 | 86 | 84 |
| huffbench | 4.436 | 1.89 | 1.90 | 1.93 | 1.89 | 58 | 25 | 21 |
| lpbench | 0.977 | 1.36 | 1.38 | 1.35 | 1.10 | 71 | 37 | 32 |
| fannkuch | 1.384 | 1.10 | 1.07 | 1.03 | 1.04 | 49 | 6 | 2 |
| recursive | 0.367 | 2.38 | 2.14 | 1.23 | 1.21 | 50 | 6 | 2 |
| heapsort | 1.616 | 0.89 | 0.87 | 0.83 | 0.86 | 111 | 66 | 63 |
| hash | 1.247 | 2.27 | 1.71 | 2.34 | 1.39 | 259 | 216 | 216 |
| lists | 1.589 | 1.95 | 2.02 | 1.97 | 1.91 | 66 | 22 | 2 |
| random | 0.976 | 1.09 | 1.09 | 1.04 | 1.05 | 50 | 5 | 2 |
| queens | 1.089 | 1.15 | 1.05 | 0.92 | 0.93 | 50 | 5 | 2 |
| hash2-cxx | 0.659 | 1.77 | 1.69 | 1.43 | 1.47 | 52 | 8 | 5 |
| stepanov-v1p2 | 2.467 | 1.05 | 1.03 | 1.00 | 1.00 | 50 | 6 | 4 |
| stepanov-abstraction | 1.457 | 1.44 | 1.12 | 1.00 | 0.99 | 43 | 6 | 4 |
| functionobjects | 1.804 | 1.71 | 1.56 | 1.29 | 1.25 | 39 | 6 | 4 |
| olden-bh | 0.386 | 2.75 | 3.00 | 1.68 | 1.61 | 57 | 13 | 6 |
| olden-health | 0.080 | 3.60 | 3.48 | 2.09 | 2.17 | 183 | 139 | 68 |
| olden-treeadd | 0.057 | 4.56 | 3.25 | 1.73 | 1.62 | 310 | 266 | 130 |
| olden-tsp | 0.362 | 2.70 | 2.30 | 1.86 | 1.74 | 180 | 136 | 66 |
| olden-em3d | 0.956 | 1.70 | 1.12 | 1.04 | 1.01 | 123 | 78 | 49 |
| olden-voronoi | 0.079 | 15.13 | 12.62 | 8.21 | 8.00 | 242 | 195 | 104 |
| ptrdist-ks | 0.203 | 1.59 | 1.80 | 1.27 | 1.42 | 52 | 6 | 2 |
| ptrdist-yacr2 | 0.166 | 3.04 | 2.33 | 1.40 | 1.29 | 57 | 11 | 5 |
| ptrdist-ft | 0.293 | 2.10 | 1.58 | 1.52 | 1.25 | 62 | 18 | 12 |
| ptrdist-anagram | 0.268 | 1.81 | 1.70 | 1.79 | 1.53 | 51 | 6 | 2 |
| tramp3d | 0.073 | 10.84 | 9.47 | 1.49 | 1.41 | 70 | 18 | 14 |
| bullet | 1.351 | 2.26 | 1.98 | 1.37 | 1.38 | 114 | 68 | 58 |
| jpeg | 0.003 | 41.39 | 29.70 | 1.87 | 1.43 | 50 | 6 | 2 |
| lame | 0.050 | 7.93 | 6.48 | 1.88 | 1.85 | 53 | 7 | 3 |
| sha | 0.011 | 7.28 | 7.29 | 1.99 | 1.00 | 50 | 6 | 2 |
| susan | 0.014 | 7.22 | 5.57 | 1.34 | 1.02 | 50 | 6 | 3 |
| mafft | 8.133 | 2.17 | 2.07 | 2.11 | 2.07 | 223 | 174 | 146 |
| nbench | 0.519 | 2.15 | 1.97 | 1.46 | 1.39 | 51 | 6 | 2 |
| ffmpeg-hevc-1t | 2.537 | 1.43 | 1.41 | 1.20 | 1.18 | 155 | 65 | 54 |
| ffmpeg-hevc-4t | 0.837 | 2.00 | 1.83 | 1.25 | 1.19 | 155 | 98 | 87 |
| ffmpeg-h264-1t | 1.432 | 3.20 | 2.04 | 2.23 | 1.43 | 155 | 39 | 29 |
| ffmpeg-h264-4t | 0.405 | 3.82 | 2.98 | 2.13 | 1.38 | 155 | 63 | 50 |

geomean JIT 2.42 -> 2.11; NativeAOT 1.72 -> 1.45 over 57 benchmarks
micro: JIT 3.43 -> 2.72, NativeAOT 3.01 -> 2.21 (11)
suite: JIT 2.21 -> 1.99, NativeAOT 1.49 -> 1.32 (42)
ffmpeg: JIT 2.43 -> 1.99, NativeAOT 1.63 -> 1.29 (4)

The start-up row's ratios are not comparable across the two runs: PF-01 timed by polling every 2 ms, which made native start-up 2 ms, and now native takes 1.3 ms; in seconds, the JIT's start-up went from 52 to 37 ms. What moved and why: callback (PF-13), mutex and atomics (PF-02), fwrite and printf (PF-03), varargs and the H.264 decodes (PF-04), salsa20 (PF-06's rotates; 8.4 to 1.05 times native as NativeAOT), and across the JIT column dynamic PGO off (PF-06). Largest remaining gaps: `long double` in software (Float80 microbenchmark, printf), variadic calls through the software `va_list` record, JIT start-up and compilation for short programs (jpeg, sha, susan, lame, tramp3d), olden-voronoi, and ffmpeg's H.264 under the JIT, whose hot functions exceed the JIT's MinOpts limits (PF-07).

**Thresholds judge each benchmark's fastest run.** ptrdist-yacr2's JIT times are bimodal (PF-04 follow-up), and a regression check on medians flagged it twice without a cause; noise only ever adds time, so `scripts/benchmark.py` now compares each benchmark's fastest run with the baseline's (`tests/benchmarks.sh` checks the logic as before), and the regression policy proposed for the owner says so. The nightly job runs three repeats instead of five to stay near an hour on GitHub's 4-core runner.

**New baseline.** `tests/perf/baseline.tsv` now holds this run's medians and fastest runs, replacing PF-01's. The M8 items made 21 benchmarks more than 20% faster under one engine or both (ratio to native), so the PF-01 baseline would no longer catch a regression that gave back what M8 gained; the proposed policy allows a baseline change in a commit that names the changes that moved it, and these are PF-02 to PF-13 above.

Gates for this change: no compiler or runtime change; `tests/run.sh benchmarks` passed with the new comparison; the last core gate (PF-06, `d2a2711`) passed 92 of 92.

## Conformance suites after M8

PF-02 (futexes), PF-04 (allocas, `localsinit`) and PF-06 change how threads, stacks and generated code behave, so the nightly conformance suites were run locally on the final compiler and runtime (`d2a2711`), each inside a memory-capped scope:

- **musl libc-test** (`tests/run.sh libc-conformance`): functional 60 of 67, regression 59 of 67, math 192 of 195, api 74 of 74; the failure list is identical to the one before M8.
- **libc++ lit suite** (`LIBCXX_LIT_JOBS=8 tests/run.sh libcxx-conformance`, 1,950 s): containers 1,809 of 1,809, strings 432 of 432, algorithms 276 of 276, utilities 1,373 of 1,374, thread 342 of 345, localization 261 of 261, input.output 574 of 587. Against the last full run (M3 to M5 close-out), utilities went from 4 failures to 1; thread keeps its 3 (`thread_create_failure`, `sleep_for.signals` and the `join.deadlock` timeout, classified then), and input.output its 13 (LC-30).
- **FCVS and the gfortran tests** (`FORTRAN_CONFORMANCE_JOBS=8 tests/run.sh fortran-conformance`): FCVS 172 of 172, gfortran 2,682 of 2,687, the same failures as before (one diagnostic now names `--system-library` instead of the removed `--native-import`).

Not run: YARPGen fuzzing (see PF-04: its native programs need up to 40 GB), and SPEC CPU, whose kits are on this host but whose portable runs take hours.
