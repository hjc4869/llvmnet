# Status

What llvmnet supports today and its known limitations. Open work and milestones are tracked in [plan/README.md](plan/README.md); measurements, with the commands that produced them, are in [evidence/](evidence/).

## Supported

- C, C++ and Fortran through Clang and Flang 22, in portable and system mode, under the JIT and NativeAOT; a relocatable toolchain package ([toolchain.md](toolchain.md)).
- Portable mode: musl, libc++/libc++abi, LLVM's OpenMP runtime and flang-rt compiled into the program, with Linux system-call semantics over .NET ([dev/host-abi.md](dev/host-abi.md)).
- System mode: the host's glibc and native libraries through generated bridges, flang-rt, and optionally a libstdc++ bitcode archive and LLVM's OpenMP runtime.
- The ffmpeg profile in [../config/ffmpeg-browser.sh](../config/ffmpeg-browser.sh): ffmpeg with dav1d, libxml2 and zlib, frame and slice threading, and SIMD128 HEVC kernels ([ffmpeg-threading.md](ffmpeg-threading.md), [simd128.md](simd128.md)). The integration tests compare decoded frame and audio hashes with native builds in both modes. Native assembly, hardware decoding, runtime CPU detection and networking are disabled, and the build produces a decode harness, not the `ffmpeg` command-line program.
- SPEC CPU 2017 and 2026 through the standard harness ([spec-coverage.md](spec-coverage.md)); the causes of portable-mode failures are in [spec-portable-limitations.md](spec-portable-limitations.md).
- Upstream conformance suites, run nightly: musl's libc-test, the libc++ and libc++abi suites, FCVS and the gfortran tests of llvm-test-suite (`tests/run.sh --tag conformance`).

## Known Limitations

Compiler:

- The driver is not a complete Clang driver; native linker options are rejected.
- Rejected LLVM constructs include computed goto (`indirectbr`), `callbr`, integers wider than 128 bits, `half`, `bfloat` and `fp128` (so Fortran `REAL(16)` and `__float128`), scalable vectors, the `x86_fp80` `minimum`/`maximum`, `canonicalize` and saturating-conversion intrinsics, ifuncs and inline assembly with instructions; [user/support-matrix.md](user/support-matrix.md) has the full list.
- Vector IR runs as scalar code, except through the opt-in SIMD128 header.
- `longjmp` cannot cross native frames. External thread-local globals need a bitcode definition.
- Functions with many aggregate values or `setjmp` sites can exceed CLR method limits, and very large functions may run without JIT optimization; `--diagnose-limits` reports both.
- Emitted assemblies carry no debug information; `-g` adds source locations to compiler errors.

Portable mode:

- Services .NET cannot provide are unavailable: `fork`, `exec`, sockets, hard links, FIFOs, SysV IPC, memory protection and asynchronous signals. [dev/host-abi.md](dev/host-abi.md) lists every difference from Linux.
- Only the C and C.UTF-8 locales.
- The floating-point environment is fixed: round to nearest, no exception flags.
- `__linux__` and the other host macros are not defined, so code that selects platform code by them needs a portable path.
- Windows, macOS and ARM64 hosts are not tested yet.

System mode:

- Linux x86-64 only.
- Native code cannot call translated variadic or aggregate callbacks. Thread cancellation, `pthread_exit`, signal handlers in translated code, external native TLS globals and callbacks on threads created by native libraries are incomplete.
- No native C++ library interchange: C++ library code must be linked as bitcode.
- OpenMP needs a staged LLVM OpenMP runtime on the loader path.

Workloads:

- ffmpeg's FATE suite is not run, and not every enabled decoder has a test fixture.
- SPEC runs use explicitly selected inputs and are not reportable results.
