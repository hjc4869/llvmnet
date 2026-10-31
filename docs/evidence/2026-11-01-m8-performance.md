# Evidence: M8 performance (2026-11-01)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads, `powersave` governor with boost), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang/Flang 22.1.8. Every number below was produced by a command in this repository, named with it. One section per M8 item, in the order the items were closed. There is no numeric target ([ADR-08](../plan/decisions.md#adr-08-performance-target)); the numbers are published and guarded against regressions.

## PF-01: benchmark harness and baselines

`scripts/benchmark.py` builds every benchmark of `tests/perf/benchmarks.tsv` three ways from the same sources and flags (`-O2 -fno-vectorize -fno-slp-vectorize` on both sides): natively with `clang-22` against glibc, with llvmnet in portable mode run by `dotnet` (JIT), and as a NativeAOT executable published from the same linked bitcode. A first run of every engine must reproduce the native output (or, for `ptrdist-ft`, which uses glibc's `rand`, a native musl build of the same sources); then each engine runs five times, one process at a time, in alternating order, and the median elapsed time per process is reported with user and system time and peak RSS. [dev/benchmarks.md](../dev/benchmarks.md) describes the method; the set, the thresholds and the baseline policy are [proposed for the owner](../plan/decisions.md#proposed-for-the-owner-2026-11-01-m8).

The set: 11 microbenchmarks written for this item (`tests/perf/`: malloc, fwrite, printf, atomics, mutex, calli, varargs, Float80, memcpy, alloca, callback) and a startup probe; 42 programs from `llvm-test-suite` 22.1.8 (Dhrystone, Linpack, the Misc, CoyoteBench, BenchmarkGame, Shootout and McGill kernels, three C++ abstraction benchmarks, Olden, Ptrdist, tramp3d-v4, Bullet, MiBench jpeg, lame, sha and susan, mafft, nbench); and the ffmpeg decode harness on two clips generated from ffmpeg's test pattern (1080p HEVC 10-bit and H.264, 240 frames each, one and four threads). Three candidates are left out with a reason: PAQ8p calls `fork`, sim has a prototype that contradicts its definition (rejected by design), MiBench gsm does not build natively against glibc's headers.

Building the set found a host-layer bug, fixed in its own commit (`76ec86b`): `lseek` on a standard input redirected from a file failed with `ESPIPE`, so `rewind(stdin)` did nothing and mafft's `pairlocalalign` read no sequences (2 lines of output instead of 1,449,796).

`tests/benchmarks.sh` (core tag) builds the microbenchmarks in both forms on every pull request, checks their output under the JIT, and checks the thresholds on synthetic results: an unchanged run, a run within tolerance and runs on hosts two and three times slower or faster pass; one benchmark 40% slower (also on a slower host) and every benchmark 15% slower fail. The nightly job in `.github/workflows/ci.yml` ends with `python3 scripts/benchmark.py --output artifacts/bench/nightly`, uploads `results.tsv`, `summary.md` and `metadata.json`, and fails on a confirmed regression. **Pending:** the first GitHub run of the nightly job, which needs the repository on GitHub (FD-19); until then the job was run locally as below.

Baseline run (`python3 scripts/benchmark.py --update-baseline --jobs 12 --output artifacts/bench/pf01-baseline`, on commit `7ca67bb` plus the harness and the `lseek` fix, the machine otherwise idle apart from the desktop): 58 benchmarks built in 252 s, every output matched, 31 minutes in all. Its medians are `tests/perf/baseline.tsv`. Ratios are llvmnet time over native time per process, so the JIT column includes startup and compilation; the peak RSS columns are the largest of the five runs.

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

NativeAOT, which runs the same translated code without a JIT, scales as native does; the JIT engine's shortfall matches a serial part of about half a second, the process start and the compilation of the decoder before the threads run compiled code (3.158 s = 0.5 + 2.66; at four threads 0.5 + 2.66 / 2.72 = 1.48 s, measured 1.57 s). A CPU-sampling profile of the four-thread HEVC decode under the JIT (`dotnet-trace collect --profile dotnet-sampled-thread-time`, 8.3 s of thread time) agrees: threads spent 1.93 s blocked in `futex` waits, the frame threads waiting for each other's progress as they do natively, but only 0.11 s of CPU (2.3% of 4.7 s) went to the futex code and monitor contention. The largest CPU item in that profile is not threading at all: 28% (1.31 s) is `Buffer.ZeroMemoryInternal`, the zeroing of the motion-compensation functions' stack buffers on every call, which PF-04 takes up.

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

**printf.** A profile of the printf microbenchmark (`%d %s %.3f %08x %ld`, 5 million lines) put 3.2 s of its 7.4 s of CPU in `Float80.Compare`: musl formats floating point with `long double` arithmetic, and the comparison scaled both operands to a common exponent as `BigInteger` values, thousands of bits wide when one operand is zero. It now compares sign, normalized binary exponent and significand directly (exact for zeros of both signs, denormals, unnormals and infinities, checked against the `BigInteger` order over all pairs of 310 encodings in `Float80Tests.ComparisonsFollowTheExactValues`). Almost all the rest, 49% before and 97% after, is the zeroing of `printf_core`'s 7 KB stack buffer on every call, which PF-04 removes.

**Before and after** (`python3 scripts/benchmark.py --only 'printf,fwrite,float80,varargs,mafft,lame,ptrdist-yacr2,olden-voronoi,almabench' --output artifacts/bench/pf03-after`, against the PF-01 baseline):

| Benchmark | Native (s) | JIT before | JIT after | NativeAOT before | NativeAOT after |
| --- | ---: | ---: | ---: | ---: | ---: |
| fwrite | 0.412 | 1.027 | 0.756 | 0.906 | 0.580 |
| printf | 0.888 | 5.681 | 4.643 | 6.235 | 5.179 |
| float80 | 0.006 | 1.717 | 1.518 | 1.487 | 1.365 |
| olden-voronoi | 0.080 | 1.377 | 0.986 | 0.747 | 0.641 |
| mafft (47 MB of output) | 8.021 | 17.376 | 16.467 | 16.907 | 16.529 |

The other benchmarks of that run (varargs, lame, ptrdist-yacr2, almabench) are within 0.95 to 1.07 of the baseline. Gates: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 92 passed, 0 failed in 503 s (with the rebuilt sysroot: 1,347 of 1,347 musl sources); unit tests 114 passed.
