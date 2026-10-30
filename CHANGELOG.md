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
- Differential fuzzing test `tests/fuzz.sh` with pinned csmith and YARPGen, run nightly (FD-16).
- The portable sysroot `sysroot/dotnet64-v2`: musl 1.2.5, libc++/libc++abi and flang-rt 22.1.8 compiled to LLVM bitcode by `scripts/build-sysroot.sh`, which `ensure-built.sh` runs (LC-15, CX-07, FT-05), and compiler-rt's complex arithmetic builtins (LC-29).
- A host layer with Linux system-call semantics: pipes, `poll`/`select`, symbolic links, shared file mappings, `posix_spawn`/`system`/`popen`, signal masks and signals from outside, resource limits, robust mutexes, thread cancellation; contract in `docs/dev/host-abi.md` (LC-04 to LC-12, LC-28).
- `-nostdlib`, `-nodefaultlibs`, `-nostartfiles`, `-fsyntax-only`; `-x ir -c` accepts `.ll` and `.bc` inputs; `--portable-sysroot` (LC-16).
- Conformance runs: `tests/libc-conformance.sh` (musl libc-test), `tests/libcxx-conformance.sh` (libc++ lit suite), `tests/fortran-conformance.sh` (FCVS and gfortran), nightly in CI (LC-19, CX-08, FT-06).
- `abort` prints the C call stack; `LLVMNET_CHECK_DESCRIPTORS=1` reports descriptors open at exit.

### Changed

- **Breaking:** `--runtime=portable` is the default, and the portable ABI tag is `dotnet64-v2`, the musl layout with glibc-width fast integer types; `dotnet64-v1` objects are rejected (LC-26, ADR-09).
- **Breaking:** `--runtime=managed-host` is removed, and link inputs without an ABI tag are rejected (ADR-04).
- System mode links flang-rt built against glibc instead of the C# Fortran runtime (FT-09).
- Portable C++ takes `type_info`, `dynamic_cast` and catch matching from libc++abi (CX-03); main-thread `thread_local` destructors run before `atexit` handlers as in glibc (CX-12).

- Identical inputs now produce byte-identical assemblies (BE-05).
- Atomics are lock-free on naturally aligned locations, about five times faster under contention (BE-16).
- `atexit` and C++ static destructors run interleaved in reverse registration order, destructor attributes last (BE-08).
- Direct calls may exchange `ptr` and `i64` in portable mode too.

### Removed

- The hand-written C library, the C# Fortran runtime, the hand-written portable headers and the MathNet.Numerics dependency (LC-17).

### Fixed

- Out-of-range `long double` to integer conversions threw instead of producing the x87 integer-indefinite value.

- System-mode `exit()` skipped `atexit` handlers and all destructors.
- `long double` stores overwrote the 6 bytes after the value.
- Wide float-to-integer conversions threw for legal and out-of-range inputs.
- `lround`/`llrint`/`is.fpclass` on `long double` produced invalid IL.
- Output names such as `System.dll` collided with framework assemblies at run time; they are now rejected.

## [0.2.0] - 2026-09-08

Prototype baseline: C, C++ and Fortran through Clang/Flang 22 to CIL; `managed-host`, `system` and `portable` runtime modes; NativeAOT output; relocatable toolchain package; threaded and SIMD128 ffmpeg profile; SPEC CPU 2017/2026 test-input coverage in system mode. See [docs/status.md](docs/status.md).
