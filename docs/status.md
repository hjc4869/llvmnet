# Status

What llvmnet supports today and its known limitations. Open work and milestones are tracked in [plan/README.md](plan/README.md); measurements, with the commands that produced them, are in [evidence/](evidence/).

## Supported

- C, C++ and Fortran through Clang and Flang 22, in portable and system mode, under the JIT and NativeAOT; a relocatable toolchain package ([toolchain.md](toolchain.md)).
- Portable images run on Linux x64, Linux ARM64, Windows x64 and macOS ARM64, under the JIT and as NativeAOT executables published on each host or cross-published from Linux ([toolchain.md](toolchain.md#supported-hosts)); the `tests/xp/` bundle checks every host against native output.
- Portable mode: musl, libc++/libc++abi, LLVM's OpenMP runtime and flang-rt compiled into the program, with Linux system-call semantics over .NET ([dev/host-abi.md](dev/host-abi.md)).
- System mode: the host's glibc and native libraries through generated bridges, flang-rt, and optionally a libstdc++ bitcode archive and LLVM's OpenMP runtime.
- The ffmpeg profile in [../config/ffmpeg-browser.sh](../config/ffmpeg-browser.sh): ffmpeg with dav1d, libxml2 and zlib, frame and slice threading, and SIMD128 HEVC kernels ([ffmpeg-threading.md](ffmpeg-threading.md), [simd128.md](simd128.md)). The integration tests compare decoded frame and audio hashes with native builds in both modes. Native assembly, hardware decoding, runtime CPU detection and networking are disabled, and the build produces a decode harness, not the `ffmpeg` command-line program.
- SPEC CPU 2017 and 2026 through the standard harness ([spec-coverage.md](spec-coverage.md)); the causes of portable-mode failures are in [spec-portable-limitations.md](spec-portable-limitations.md).
- Upstream conformance suites, run nightly: musl's libc-test, the libc++ and libc++abi suites, FCVS and the gfortran tests of llvm-test-suite (`tests/run.sh --tag conformance`).

## Known Limitations

[user/limitations.md](user/limitations.md) is the one page of what llvmnet does not do: the LLVM constructs the compiler rejects and the system calls the portable host refuses are generated from the code (BE-02 and the runtime's own table), and the remaining limits of each mode, of the other hosts and of the workload gates are listed with it.
