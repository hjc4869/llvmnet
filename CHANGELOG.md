# Changelog

All notable user-visible changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the compiler follows [Semantic Versioning](https://semver.org/) as described in [docs/dev/versioning.md](docs/dev/versioning.md). Each entry names the plan item it implements, such as `FD-05`.

## [Unreleased]

### Added

- Apache-2.0 WITH LLVM-exception license, third-party notices, contribution guide and this changelog (FD-01 to FD-04).
- `--version` reports the build and LLVM versions; emitted assemblies record them and refuse an incompatible runtime (FD-05).
- Pinned upstream sources through `scripts/fetch-sources.sh` (FD-06).
- Parallel test runner `tests/run.sh`, unit tests, JIT validation of emitted assemblies, CI workflow and dev container (FD-11 to FD-15, FD-19).
- Source-located diagnostics, kept IR on failure and exit codes 1/2/3 (BE-04).
- `--diagnose-limits` (BE-25) and `--check-indirect-calls` (BE-18).
- 128-bit atomics; 65- to 128-bit integer intrinsics; constrained floating-point intrinsics; masked memory intrinsics; `llvm.objectsize`, `exp10`, `canonicalize`, `minimumnum`/`maximumnum`, saturating shifts.
- Global aliases, undefined weak symbols and alias exports from managed libraries (BE-07).
- Experimental `--portable-libc` with musl, libc++/libc++abi and flang-rt (LC-01, LC-02, CX-01, FT-01).

### Changed

- Identical inputs now produce byte-identical assemblies (BE-05).
- Atomics are lock-free on naturally aligned locations, about five times faster under contention (BE-16).
- `atexit` and C++ static destructors run interleaved in reverse registration order, destructor attributes last (BE-08).
- Direct calls may exchange `ptr` and `i64` in portable mode too.

### Fixed

- System-mode `exit()` skipped `atexit` handlers and all destructors.
- `long double` stores overwrote the 6 bytes after the value.
- Wide float-to-integer conversions threw for legal and out-of-range inputs.
- `lround`/`llrint`/`is.fpclass` on `long double` produced invalid IL.
- Output names such as `System.dll` collided with framework assemblies at run time; they are now rejected.

## [0.2.0] - 2026-09-08

Prototype baseline: C, C++ and Fortran through Clang/Flang 22 to CIL; `managed-host`, `system` and `portable` runtime modes; NativeAOT output; relocatable toolchain package; threaded and SIMD128 ffmpeg profile; SPEC CPU 2017/2026 test-input coverage in system mode. See [docs/status.md](docs/status.md).
