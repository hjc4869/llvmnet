# Evidence: M9 workload gates (2026-11-01)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang/Flang 22.1.8, starting from commit `d776c33`. Every heavy run below ran inside a memory-capped user scope (`systemd-run --user --scope -p MemoryMax=24G -p MemorySwapMax=0`, 32 GB for llvm-test-suite). One section per M9 item, in the order the items were closed. A suite's pass rate counts only the tests that pass natively (clang-22, flang-22, glibc), so it measures parity with a native build, not the suite's own expectations.

## WG-05: ffmpeg threaded and SIMD128 profiles in the nightly

The nightly job already ran `tests/ffmpeg-threads.sh` and `tests/ffmpeg-simd128.sh` (tag `integration`), but the threaded profile checked NativeAOT only when `FFMPEG_THREADS_AOT=1` was set, and the nightly did not set it. The test now publishes and runs both NativeAOT decoders by default (`FFMPEG_THREADS_AOT=0` checks the JIT only, for a host without the NativeAOT tools), so the nightly covers both ABIs under the JIT and NativeAOT in both profiles.

| Test | Command | Checks | Result | Time |
| --- | --- | --- | --- | ---: |
| Threaded decoding | `FFMPEG_THREADS_AOT=1 tests/run.sh ffmpeg-threads` | H.264, HEVC, VP9 and AV1 (dav1d) fixtures; frame and slice threading at two and four threads; decoder drain and early teardown (frame limit 3); hashes equal to the native single-thread decode | 96 checks pass: 48 JIT and 48 NativeAOT, system and portable | 207 s |
| SIMD128 profile | `tests/run.sh ffmpeg-simd128` | DSP kernels under the JIT, with hardware intrinsics off and as NativeAOT; 8- and 10-bit HEVC with frame and slice threading under the JIT, NativeAOT and the software fallback, plus decode-only mode | 30 checks pass, 8 of them NativeAOT decodes, system and portable | 176 s |

Both decoders of both ABIs have no explicit P/Invoke imports in portable mode (`LlvmNet.Checks inspect`), as before.

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
