# Evidence: M9 workload gates (2026-11-01)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang/Flang 22.1.8, starting from commit `d776c33`. Every heavy run below ran inside a memory-capped user scope (`systemd-run --user --scope -p MemoryMax=24G -p MemorySwapMax=0`, 32 GB for llvm-test-suite). One section per M9 item, in the order the items were closed. A suite's pass rate counts only the tests that pass natively (clang-22, flang-22, glibc), so it measures parity with a native build, not the suite's own expectations.

## WG-05: ffmpeg threaded and SIMD128 profiles in the nightly

The nightly job already ran `tests/ffmpeg-threads.sh` and `tests/ffmpeg-simd128.sh` (tag `integration`), but the threaded profile checked NativeAOT only when `FFMPEG_THREADS_AOT=1` was set, and the nightly did not set it. The test now publishes and runs both NativeAOT decoders by default (`FFMPEG_THREADS_AOT=0` checks the JIT only, for a host without the NativeAOT tools), so the nightly covers both ABIs under the JIT and NativeAOT in both profiles.

| Test | Command | Checks | Result | Time |
| --- | --- | --- | --- | ---: |
| Threaded decoding | `FFMPEG_THREADS_AOT=1 tests/run.sh ffmpeg-threads` | H.264, HEVC, VP9 and AV1 (dav1d) fixtures; frame and slice threading at two and four threads; decoder drain and early teardown (frame limit 3); hashes equal to the native single-thread decode | 96 checks pass: 48 JIT and 48 NativeAOT, system and portable | 207 s |
| SIMD128 profile | `tests/run.sh ffmpeg-simd128` | DSP kernels under the JIT, with hardware intrinsics off and as NativeAOT; 8- and 10-bit HEVC with frame and slice threading under the JIT, NativeAOT and the software fallback, plus decode-only mode | 30 checks pass, 8 of them NativeAOT decodes, system and portable | 176 s |

Both decoders of both ABIs have no explicit P/Invoke imports in portable mode (`LlvmNet.Checks inspect`), as before.
