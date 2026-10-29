# Changelog

All notable user-visible changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the compiler follows [Semantic Versioning](https://semver.org/) as described in [docs/dev/versioning.md](docs/dev/versioning.md). Each entry names the plan item it implements, such as `FD-05`.

## [Unreleased]

### Added

- Apache-2.0 WITH LLVM-exception license, third-party notices, contribution guide and this changelog (FD-01 to FD-04).

## [0.2.0] - 2026-09-08

Prototype baseline: C, C++ and Fortran through Clang/Flang 22 to CIL; `managed-host`, `system` and `portable` runtime modes; NativeAOT output; relocatable toolchain package; threaded and SIMD128 ffmpeg profile; SPEC CPU 2017/2026 test-input coverage in system mode. See [docs/status.md](docs/status.md).
