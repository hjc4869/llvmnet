# llvmnet

llvmnet compiles C, C++ and Fortran to .NET. Clang and Flang 22 produce LLVM bitcode, llvmnet links it and translates it to CIL, and the result is a .NET assembly, a launcher for one, or a NativeAOT executable, running as ordinary .NET code on Linux, Windows and macOS. Unmodified musl, libc++ and flang-rt are compiled into portable programs, so a C program sees the C library it expects and the operating system through a small host layer; a second mode links against the native Linux ABI instead.

The project is at version 0.x and not yet production-ready. What it does today is checked by tests and recorded with dates and commands under [docs/evidence/](docs/evidence/): the upstream conformance suites of musl, libc++ and Flang pass in portable mode at their documented thresholds (each remaining failure has a named cause), ffmpeg decodes media identically to native builds, every SPEC CPU 2017 and 2026 entry passes in both modes or is a documented exclusion, and the same portable images run on Linux x64 and ARM64, Windows x64 and macOS ARM64. [docs/user/limitations.md](docs/user/limitations.md) is the list of what does not work, and [docs/plan/README.md](docs/plan/README.md) the plan towards 1.0.

## Install

- **A release package** (Linux x86-64): `llvmnet-<version>-linux-x64-llvm.tar.xz` from a release (`scripts/release-artifacts.sh` builds them) has everything needed to compile; the thin `llvmnet-<version>-linux-x64.tar.xz` expects LLVM, Clang and Flang 22 on `PATH`. Running the programs needs the .NET 10 runtime.
- **The dotnet tool**: `dotnet tool install -g llvmnet --add-source <directory with llvmnet.<version>.nupkg>`, with LLVM 22 on `PATH`.
- **From source** (Debian 13 or compatible): the .NET 10 SDK and LLVM 22 with development files (`scripts/ci/install-packages.sh` installs them), then `bash scripts/ensure-built.sh`.

[docs/user/getting-started.md](docs/user/getting-started.md) walks through each.

## Use

```sh
llvmnet-clang hello.c -o hello.dll          # a .NET assembly
dotnet hello.dll

llvmnet-clang++ -std=c++20 -O2 app.cpp -o app    # a launcher plus app.llvmnet/
llvmnet-flang -O3 solver.f90 --nativeaot -o solver   # a native executable
llvmnet-clang --runtime=system client.c --system-library ./libnative.so -o client.dll
```

The command line follows Clang's, so `CC=llvmnet-clang CXX=llvmnet-clang++ FC=llvmnet-flang` works with Makefiles, and `cmake/llvmnet.cmake` and `meson/llvmnet.ini` with CMake and Meson; objects and archives are LLVM bitcode. [docs/user/driver.md](docs/user/driver.md) lists every option with what the driver does with it.

| Mode | ABI | Runs on | Libraries |
| --- | --- | --- | --- |
| `--runtime=portable` (default) | `dotnet64-v2`, fixed and independent of the host | every supported .NET host, JIT or NativeAOT | musl, libc++, LLVM's OpenMP runtime and flang-rt compiled into the program; system services from `LlvmNet.Runtime`; host libraries such as EGL and OpenGL ES through [native extensions](docs/user/extensions.md) |
| `--runtime=system` | `system-linux-x64-v1`, the native Linux x86-64 ABI | Linux x64 | the host's glibc and native libraries through generated bridges |

[docs/user/modes.md](docs/user/modes.md) defines both contracts. Generated code is unsafe and is not a sandbox: compile and run only code you trust.

## Documentation

For users, in [docs/user/](docs/user/): [getting started](docs/user/getting-started.md), the [driver reference](docs/user/driver.md), [runtime modes and ABI contracts](docs/user/modes.md), the frozen [portable ABI](docs/user/abi.md), [linking](docs/user/linking.md) bitcode, managed and native libraries, [native extensions](docs/user/extensions.md) for host libraries in portable programs, [deployment](docs/user/deployment.md) (what to ship, NativeAOT, hosts, release packages), [limitations](docs/user/limitations.md), [troubleshooting](docs/user/troubleshooting.md), the [LLVM IR support matrix](docs/user/support-matrix.md), the [SIMD128](docs/user/simd128.md) path, [x86 intrinsics](docs/user/x86-intrinsics.md), and the [support and deprecation policy](docs/user/support.md). [SECURITY.md](SECURITY.md) says what llvmnet does and does not protect against and how to report a vulnerability.

For contributors, in [docs/dev/](docs/dev/): the [architecture](docs/dev/architecture.md), the [host interface](docs/dev/host-abi.md) of portable programs, [calls and signatures](docs/dev/calling-convention.md), the [C++ exception bridge](docs/dev/cxx-eh-bridge.md), [versioning](docs/dev/versioning.md), [testing](docs/dev/testing.md), [adding a host function](docs/dev/adding-a-host-function.md), [writing a native extension](docs/dev/extensions.md), the [release process](docs/dev/release-process.md), the [gates](docs/dev/gates.md) and their thresholds, the [artifacts directory](docs/dev/artifacts.md), the [SPEC CPU](docs/dev/spec.md) and [ffmpeg](docs/dev/ffmpeg.md) workloads and the [video benchmark](docs/dev/video-benchmark.md). [CONTRIBUTING.md](CONTRIBUTING.md) says how to build, run the gates and add a test.

Evidence, dated and produced by commands in the repository, in [docs/evidence/](docs/evidence/); the production-readiness plan, its decisions and the status board in [docs/plan/](docs/plan/README.md).

## Tests

```sh
tests/run.sh --tag core --tag toolchain --exclude-tag slow   # the pull-request gate
dotnet test tests/LlvmNet.UnitTests -c Release
tests/run.sh --list                                          # every test with its tags and prerequisites
```

The runner builds once, runs the tests in parallel and writes per-test logs, `summary.tsv` and JUnit `results.xml` under `artifacts/tests/results/`; [docs/dev/testing.md](docs/dev/testing.md) describes the tags, the oracles and the nightly suites.

## License

llvmnet is licensed under the MIT License; see [LICENSE.TXT](LICENSE.TXT). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Contributions are accepted under the same license; see [CONTRIBUTING.md](CONTRIBUTING.md).
