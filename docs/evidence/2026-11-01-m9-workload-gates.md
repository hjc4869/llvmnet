# Evidence: M9 workload gates (2026-11-01)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang/Flang 22.1.8, starting from commit `d776c33`. Every heavy run below ran inside a memory-capped user scope (`systemd-run --user --scope -p MemoryMax=24G -p MemorySwapMax=0`, 32 GB for llvm-test-suite). One section per M9 item, in the order the items were closed. A suite's pass rate counts only the tests that pass natively (clang-22, flang-22, glibc), so it measures parity with a native build, not the suite's own expectations.

## WG-05: ffmpeg threaded and SIMD128 profiles in the nightly

The nightly job already ran `tests/ffmpeg-threads.sh` and `tests/ffmpeg-simd128.sh` (tag `integration`), but the threaded profile checked NativeAOT only when `FFMPEG_THREADS_AOT=1` was set, and the nightly did not set it. The test now publishes and runs both NativeAOT decoders by default (`FFMPEG_THREADS_AOT=0` checks the JIT only, for a host without the NativeAOT tools), so the nightly covers both ABIs under the JIT and NativeAOT in both profiles.

| Test | Command | Checks | Result | Time |
| --- | --- | --- | --- | ---: |
| Threaded decoding | `FFMPEG_THREADS_AOT=1 tests/run.sh ffmpeg-threads` | H.264, HEVC, VP9 and AV1 (dav1d) fixtures; frame and slice threading at two and four threads; decoder drain and early teardown (frame limit 3); hashes equal to the native single-thread decode | 96 checks pass: 48 JIT and 48 NativeAOT, system and portable | 207 s |
| SIMD128 profile | `tests/run.sh ffmpeg-simd128` | DSP kernels under the JIT, with hardware intrinsics off and as NativeAOT; 8- and 10-bit HEVC with frame and slice threading under the JIT, NativeAOT and the software fallback, plus decode-only mode | 30 checks pass, 8 of them NativeAOT decodes, system and portable | 176 s |

Both decoders of both ABIs have no explicit P/Invoke imports in portable mode (`LlvmNet.Checks inspect`), as before.

## WG-02: gcc-c-torture and c-testsuite

Two suites, both against a native build of the same source:

- **c-testsuite** (pinned at commit `5c72756` in `scripts/sources.lock`, fetched by hash): 220 single-file programs, each with the output it must print and exit status 0. `tests/c-testsuite.sh` (tag `conformance`) builds each with `clang-22` and with llvmnet and compares. All 220 pass natively.
- **gcc-c-torture execute tests**: the copy in the pinned `llvm-test-suite` 22.1.8 (`SingleSource/Regression/C/gcc-c-torture`), built through that suite's CMake with its list of GCC extensions Clang does not implement (nested functions, VLAs in structures and others), so the set is the one native Clang builds: 1,488 tests, all passing natively. They run as part of `tests/llvm-test-suite.sh` (WG-01 below), which reports them as their own group.

| Suite | Mode | Passed | Counted | Rate | Failures by cause |
| --- | --- | ---: | ---: | ---: | --- |
| c-testsuite | portable | 219 | 220 | 99.5% | `00025` declares `int strlen(char *)` against the C library's definition, a contradicting prototype (owner exclusion, 2026-10-31) |
| c-testsuite | system | 220 | 220 | 100% | |
| gcc-c-torture | portable | 1,473 | 1,488 | 99.0% (99.2% without the 3 x86 tests) | computed goto (9), `__builtin_setjmp` (2), `__builtin_return_address` (1), x86 tests that do not apply to the portable target (3: x87 control-word assembly, and two that compare a `long double`'s padding unless `__x86_64__` is defined) |
| gcc-c-torture | system | 1,475 | 1,488 | 99.1% | computed goto (9), `__builtin_setjmp` (2), `__builtin_return_address` (1), x87 control-word inline assembly (1) |

The first runs found three defects in llvmnet, each fixed in its own commit with a test: empty `asm` statements with an output tied to an input (an optimization barrier 12 torture tests use) were rejected (`281ec9c`); a K&R `main(argc)` was rejected (`281ec9c`); and `__builtin_setjmp` is now listed as rejected in the support matrix and the limitations page, where it was missing.

## WG-01: llvm-test-suite through its own CMake

`tests/llvm-test-suite.sh` (tag `conformance`) configures `llvm-test-suite` 22.1.8's `SingleSource` and `MultiSource` with `cmake/llvmnet.cmake` and `llvmnet-clang`/`llvmnet-clang++` wrappers for one mode each, builds with Ninja and runs the suite's own lit configuration against its reference outputs (`TEST_SUITE_USER_MODE_EMULATION`, so the suite's tools are host builds and the programs, launchers, run directly). The same directories built natively with `clang-22` against glibc define which tests count: all 2,049 pass natively. The NativeAOT subset publishes every program of `SingleSource/Benchmarks` as a NativeAOT executable. Run in a 32 GB memory-capped scope with 12 jobs (3 for NativeAOT): the three modes take about 75 minutes.

`tests/conformance/llvm-test-suite.tsv` names the class of every known failure. Two kinds are left out of the adjusted rate: x86 instruction-set tests in portable mode, which has no x86 instruction set by design, and the owner's documented exclusions as for SPEC (`fork`; glibc or libstdc++ behavior that musl and libc++ do not share; prototypes that contradict the definition). llvmnet's own limitations and open plan items count against both rates; an unclassified failure fails the test. One program, SIOD, chooses its platform code by `linux` and builds with `-D__linux__` as a per-program setting, as the SPEC benchmarks in the same position do (ADR-14).

| Mode | Passed | Counted | Rate | Without not-applicable and excluded | Unclassified |
| --- | ---: | ---: | ---: | ---: | ---: |
| portable (JIT) | 2,002 | 2,049 | 97.7% | 99.1% (29 left out) | 0 |
| system (JIT) | 2,013 | 2,049 | 98.2% | 98.8% (12 left out) | 0 |
| portable NativeAOT (`SingleSource/Benchmarks`) | 140 | 141 | 99.3% | 99.3% | 0 |

Portable failures by class: 16 llvmnet limitations (computed goto 11, `__builtin_setjmp` 2, `__builtin_return_address` 1, rounding modes other than to nearest 1, SIOD's conservative stack-scanning garbage collector 1); 2 open plan items (BE-38 half precision, BE-42 unsupported x87 encodings); 9 x86 tests; 11 contradicting prototypes; 8 glibc or libstdc++ behaviors (glibc's `random()` and `rand()` sequences in city, Obsequi and ft; `catclose` on a failed `catopen` in consumer-typeset; musl writing stdout's first line before it detects a non-terminal in clamscan; `std::set_unexpected`, removed in C++17, in libc++; `<sys/queue.h>` in CLAMR and a `__GLIBC__` test in ispell); 1 `fork` (PAQ8p). System failures by class: 22 llvmnet limitations (the same computed goto, `__builtin_setjmp`, `__builtin_return_address`, rounding and SIOD cases; inline assembly with instructions in two X86 tests, a torture test and minisat; an x86 target intrinsic, `llvm.x86.sse.max.ps`, in one SSE test; and `std::set_unexpected`, which the optional system libstdc++ archive leaves out); 2 open plan items (BE-38, BE-42); 10 contradicting prototypes; 1 `fork`; 1 test that reads uninitialized memory (beamformer leaves a filter buffer's first element unset; a fresh native heap is zero-filled, while in system mode glibc's heap is shared with the .NET runtime and hands out reused memory), left out of the adjusted rate. The system run is the second one, after the two system-mode fixes below; the first one counted cfrac and ft as failures (2,011 passed). The NativeAOT failure is computed goto. Each failure's class and reason are in the table file; `results.tsv` and `failures.txt` hold the run.

Building the suite found these defects, each fixed in its own commit with a test:

- CMake could not identify llvmnet (no compiler ID, pointer size or language standard) and llvm-test-suite stopped at configure; objects named `.obj` (CMake's Generic platform) were rejected; portable `-dumpmachine` reported the host triple, which the suite passed back as `--target`; the package lacked the CMake toolchain file the documentation named (`caff471`, TC-07).
- `llvm.vector.reduce.fmax`/`fmin` without `nnan`, from a loop vectorized by a pragma, did not compile (`281ec9c`, BE-43).
- Portable `open` with `O_CREAT` and read-only access failed with `EINVAL` (`14d0bba`, found by flang-rt's unit tests, WG-03).
- System mode: a call through a K&R declaration (cfrac's `extern int free();` calling `void free(void *)`) produced invalid IL; and `rand()` before any `srand()` gave a time-seeded sequence, because CoreCLR calls `srandom(time)` at startup and a system-mode program shares the C library with it (ft's graphs differed on every run) (`5b82538`).

It also found a limitation now on the limitations page: a conservative garbage collector that scans the C stack and the registers `setjmp` saved, as SIOD's does, cannot see pointers held in CIL locals. And one open item: BE-42, x87 encodings the 80387 rejects (pseudo-NaNs, pseudo-infinities, unnormals) are read by value, while hardware and LLVM's constant folder read them as NaN (`Float/classify`).

## WG-03: library suites with pass-rate tracking

The library suites of LC-19, CX-08 and FT-06 already ran in the nightly job, but reported only (each threshold defaulted to 0), flang-rt's own unit tests were not run, and libc++ never ran under NativeAOT. Now:

- The suites run in their own nightly job, `conformance`, so the integration job keeps its time limit, and each runs against a threshold set in the workflow ([dev/gates.md](../dev/gates.md)).
- **flang-rt's unit tests** (`tests/flang-rt-unittests.sh`): the 34 test files of `flang-rt/unittests/Runtime` with LLVM's googletest built without LLVMSupport (`GTEST_NO_LLVM_SUPPORT`), natively with `clang++-22` against Debian's flang-rt 22.1.8 and in portable mode against the sysroot's. Each of the 253 tests runs in its own process, because a test that forks aborts the whole program in portable mode. gtest has no death tests on a target it does not recognize, so `tests/support/gtest-without-death-tests.h` turns each `EXPECT_DEATH` and `EXPECT_EXIT` into a failure with the reason "death test (needs fork)".
- **libc++ under NativeAOT** (`LIBCXX_NATIVEAOT=1 tests/libcxx-conformance.sh`): every test that links is published as a NativeAOT executable; the default subset is vector, the string constructors, futures, string streams and the numeric locale facets.

| Suite | Passed | Counted | Rate | Left out |
| --- | ---: | ---: | ---: | --- |
| musl libc-test (rerun on the final compiler) | 385 | 403 | 95.5% | per group: functional 60 of 67, regression 59 of 67, math 192 of 195, api 74 of 74; 13 fail natively |
| libc++ lit suite (rerun) | 5,067 | 5,084 | 99.7% | 6 death tests that need fork; per directory from 97.8% (input.output) to 100% |
| FCVS and gfortran tests (rerun) | 2,854 | 2,859 | 99.8% | FCVS 172 of 172, gfortran 2,682 of 2,687; 16 fail natively |
| flang-rt unit tests, portable | 216 | 216 | 100% | 31 need `fork` (27 death tests, 4 asynchronous `EXECUTE_COMMAND_LINE`), 6 not compiled for an unrecognized operating system (`fdate`, `getlog`, `REAL(16)`) |
| libc++ under NativeAOT (vector, string constructors, futures, string streams, numeric facets) | 327 | 328 | 99.7% | 3 unsupported; the failure, `futures.async/thread_create_failure`, is the one the JIT run has (it expects thread creation to fail under a limit .NET does not apply) |

The first runs of the unit tests found two defects, fixed in their own commits with tests: portable `open` with `O_CREAT` and read-only access failed with `EINVAL` (`14d0bba`), and `EXECUTE_COMMAND_LINE` reported `CMDSTAT` 7 ("Killed by signal") after every successful command, because flang-rt passed `system()`'s status to musl's `WIFSIGNALED` as a 64-bit integer (`fe0a79a`, `ports/flang-rt/execute-status.patch`).

## WG-04: ffmpeg's FATE suite

`tests/ffmpeg-fate.sh` (tag `fate`, its own nightly job) builds the pinned ffmpeg 9.0.2 twice with every component and the same asm-free, offline options (`--disable-asm --disable-inline-asm --disable-hwaccels --disable-autodetect --disable-network --enable-pthreads`), natively with `clang-22` and with llvmnet in portable mode, and runs `make fate` in both against the FATE samples. The samples (2,648 files, 1.3 GB, from `rsync://fate-suite.ffmpeg.org/fate-suite/`) are fetched by hash: every file must match `tests/ffmpeg/fate-suite.sha256`, and the nightly job caches them by that file's hash. FATE compares each output with its own reference; a test counts when the native build passes it. The portable `ffmpeg` and `ffprobe` are launchers beside a 72 MB managed image and run directly, as FATE runs native programs (`ffmpeg -version` takes 0.7 s).

First run, with ffmpeg's configure unchanged (in memory-capped scopes, 12 jobs native and 8 portable):

| Build | FATE tests run | Passed | Failed |
| --- | ---: | ---: | --- |
| native (clang-22, glibc) | 5,286 | 5,285 | `fate-source` (a check of the source tree's license headers and file list) |
| portable (llvmnet) | 5,182 | 5,179 | `fate-source`, `fate-seek-cache-pipe`, `fate-seek-hls` |

The two portable failures and the 104 checkasm tests the portable build did not run have one cause, outside the translation: ffmpeg's `check_func` probes a function by linking `extern int f(); int main(void){ f(); }`, a call without the arguments the definition takes, which llvmnet rejects in portable mode as a prototype that contradicts the definition. Configure therefore left 18 functions out of the portable build only (`mkstemp`, `tempnam`, `mmap`, `isatty`, `sysconf`, `sched_getaffinity`, `usleep` and others):

- `fate-seek-cache-pipe`: without `mkstemp` or `tempnam`, the cache protocol cannot create its temporary file.
- `fate-seek-hls`: without `sysconf` and `sched_getaffinity`, `av_cpu_count()` is 1, so the MPEG-2 encoder that generates the test's input ran one slice thread where the native build ran several, and the streams differ. With the thread count fixed (`-threads 1`) both builds produce identical streams; intra-only encoding, which slices do not affect, is identical either way.

The harness now runs both builds from a copy of configure whose probe takes the function's address instead of calling it, so the two configurations agree. The probe's form is common: autoconf's `AC_CHECK_FUNCS` also declares the function with a `char` result and calls it with no arguments, so autotools projects configured in portable mode would lose functions the same way; this is [proposed for the owner](../plan/decisions.md#proposed-for-the-owner-2026-11-01-m9) as part of the build-system item TC-07.

Final run, `tests/ffmpeg-fate.sh` with the patched probe (`LLVMNET_SKIP_BUILD=1 FFMPEG_FATE_JOBS=12`, 24 GB memory cap, about 35 minutes: native configure, build and FATE 2 minutes, portable configure 3, build 5, FATE 22):

| Build | FATE tests run | Passed | Failed |
| --- | ---: | ---: | --- |
| native | 5,286 | 5,285 | `fate-source` |
| portable | 5,286 | 5,285 | `fate-source` |

Every test the native build passes, the portable build passes: 5,285 of 5,285. The configurations now differ only in what the portable target does not have by design: Linux kernel headers (`asm/types.h`, `linux/perf_event.h`, V4L2, OSS) and x86 features (`MM_EMPTY`, `EBP_AVAILABLE`).

## Test gates for M9

Every commit that changed the compiler, the runtime or the sysroot ran after a green core and toolchain gate (`tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8`, in a 32 GB memory cap), and the unit tests:

| Run | Before | Result |
| --- | --- | --- |
| Gate 1 | `caff471`, `281ec9c`, `14d0bba` (driver, CMake file, tied `asm`, `main(argc)`, read-only `O_CREAT`) | 93 passed in 614 s |
| Gate 2 | the same with BE-43's reductions | 93 passed in 524 s |
| Gate 3 | `5b82538`, `fe0a79a`, `a99eb40` (native call results, the random seed, the flang-rt patch with a sysroot rebuild, the ABI freeze test) | 94 passed in 570 s |
| Unit tests | after each change to the compiler or runtime | 130 passed |
| `tests/deployment.sh` (slow) | the packaged CMake toolchain file | passed |
| `dotnet format whitespace --verify-no-changes`, `shellcheck -S error` | the pull-request gate's other steps | clean, after fixing one attribute placement from PF-14 (`78bc36e`) |
| Link check over all Markdown files | after the documentation changes | 59 files, no broken link (three stale anchors fixed in `fe541ec`) |

The library suites were run again on the final compiler and runtime (`tests/run.sh -j 1 libc-conformance fortran-conformance libcxx-conformance`, 40 GB memory cap, 59 minutes), with the same results and the same failures as at the M8 close: libc-test functional 60 of 67, regression 59 of 67, math 192 of 195, api 74 of 74 (128 s); FCVS 172 of 172 and the gfortran tests 2,682 of 2,687 (1,515 s); libc++ containers 1,809 of 1,809, strings 432 of 432, algorithms 276 of 276, utilities 1,373 of 1,374, thread 342 of 345, localization 261 of 261, input.output 574 of 587 (1,913 s). c-testsuite on the final compiler: as above.
