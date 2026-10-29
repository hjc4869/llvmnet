# Workstream WG: Workload and conformance gates

Prefix `WG`. Owner: unassigned. Milestone: [M9](README.md#m9-workload-gates-and-10). These are the suites whose results define "production-ready"; the individual library workstreams reference them.

## Current state (verified 2026-10-30)

- ffmpeg browser profile with dav1d, libxml2 and zlib: 31 media fixtures, DASH/HLS, scaling/resampling, frame/slice/dav1d threading at two and four threads, SIMD128 HEVC kernels, and the full 1,150-frame 4K HDR hash comparison, in system and portable modes under JIT and NativeAOT. The upstream FATE suite has not been run and the `ffmpeg`/`ffprobe` command-line tools are not built.
- SPEC CPU 2017 and 2026: every installed entry (101 distinct benchmarks) validated in system mode on test inputs; portable 20 of 101; mcf validated through refrate in system mode; no full train/ref matrix, no reportable configuration review.
- No generic C/C++/Fortran conformance suite runs today.

## Items

- [ ] **WG-01** (M, P0) `llvm-test-suite` SingleSource and MultiSource (C and C++) through its CMake with `CMAKE_C_COMPILER=llvmnet-clang`, in portable JIT, portable NativeAOT (subset) and system mode, using its reference outputs; track pass rate; target at or above 99% with each failure classified.
- [ ] **WG-02** (M, P0) GCC `c-torture` execute tests and `c-testsuite` single-exec tests in portable mode with pass-rate tracking.
- [ ] **WG-03** (M, P0) Library suites: the musl project's `libc-test` ([LC-19](02-portable-c-library.md)), libc++ lit directories ([CX-08](03-cxx-runtime.md)), flang-rt unit tests and FCVS ([FT-06](04-fortran-runtime.md)).
- [ ] **WG-04** (M, P0) ffmpeg FATE: build the real `ffmpeg`/`ffprobe` programs in portable mode and run the asm-free, offline FATE subset (`make fate-*` with `TARGET_EXEC` pointing at the launcher) against the FATE sample suite fetched by hash, nightly; the private 4K HDR full-file gate stays as an opt-in local check.
- [ ] **WG-05** (M, P0) ffmpeg threaded and SIMD128 profiles in both ABIs under JIT and NativeAOT (the existing `tests/ffmpeg-threads.sh` and `tests/ffmpeg-simd128.sh`) in the nightly.
- [ ] **WG-06** (L, P0) SPEC CPU 2017 and 2026 on the private runner: system and portable, `test` then `train` then `ref`, one copy, JIT and NativeAOT; a completed matrix record per toolchain build; portable at parity with system; deviations documented per benchmark; a reportability review of flags and system description without publishing scores unless the project owner decides to.
- [ ] **WG-07** (M, P1) Ecosystem smoke ([TC-07](06-driver-and-toolchain.md)): zlib, bzip2, xz, sqlite, lua, zstd, libpng, libjpeg-turbo (no-asm), expat and pcre2 built with their own build systems and running their own test suites in portable mode.
- [ ] **WG-08** (M, P1) Stability: a 24-hour soak of threaded decoding with RSS and descriptor-count monitoring, plus `ulimit` behaviors (stack, open files).
- [ ] **WG-09** (S, P1) `docs/dev/gates.md`: which suites run on PR, nightly and release, with thresholds, mapped to the runner tags in FD-12.

## Exit criteria for M9

WG-01 through WG-06 report at or above their thresholds on the release candidate build for two consecutive nightly runs.
