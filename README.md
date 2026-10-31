# llvmnet

llvmnet compiles C, C++ and Fortran through Clang and Flang 22 to LLVM bitcode, then translates the linked bitcode to unsafe .NET 10 CIL: a .NET assembly, a Unix launcher for one, or a NativeAOT executable. C, C++ and Fortran share one CIL backend, and the translated code runs as ordinary .NET methods.

The project is experimental. [docs/status.md](docs/status.md) summarizes what works and the known limitations; [docs/plan/README.md](docs/plan/README.md) tracks the work towards 1.0.

## Build

Requirements (Linux x86-64):

- .NET 10 SDK.
- LLVM, Clang and Flang 22 with development files: `clang-22`, `clang++-22`, `flang-22`, `opt-22`, `llvm-config-22`, `llvm-ar-22`, `llvm-ranlib-22`, `llvm-nm-22` and `libLLVM-22.so`. On Debian 13, `scripts/ci/install-packages.sh` (run as root) installs everything the build and tests use.
- Network access for the first build: the portable sysroot is built from pinned musl and LLVM sources that `scripts/fetch-sources.sh` downloads into `artifacts/vendor/`.
- For the ffmpeg integration: CMake, Ninja, Meson and pkg-config.

```sh
bash scripts/ensure-built.sh
mkdir -p artifacts
bin/llvmnet tests/c/runtime.c -o artifacts/example.dll
dotnet artifacts/example.dll payload
```

`scripts/ensure-built.sh` builds the solution, including the compiler's C++ bridge to LLVM, and runs `scripts/build-sysroot.sh`, which rebuilds the sysroots only when their inputs change. Keep an emitted assembly together with the files the compiler writes next to it: its `.runtimeconfig.json`, `LlvmNet.Runtime.dll` and, in system mode, the native bridge `lib<name>.llvmnet-host.so`. LLVM and the bridge are needed to compile, not to run the result.

## Use

```sh
bin/llvmnet -O1 -c source.c -o source.o
llvm-ar-22 rcs libexample.a source.o
bin/llvmnet main.c -L. -lexample -o program.dll
dotnet program.dll argument

bin/llvmnet -std=c++17 program.cpp -o cpp-program.dll
bin/llvmnet program.f90 -o fortran-program.dll
bin/llvmnet -S -emit-llvm source.c -o source.ll
bin/llvmnet main.c libexample.a --emit-llvm -o program.bc
```

Objects and archives contain LLVM bitcode. Explicit object files are always linked; archive members are linked when they resolve undefined symbols, with rescanning for dependencies between archives. `-lc`, `-lm`, `-lpthread`, `-ldl` and `-lrt` need no archive in either mode; portable mode also provides `-lc++` and the Fortran runtime.

`bin/llvmnet --help` lists the driver options. `--keep-ir linked.bc` keeps the linked bitcode, `LLVMNET_TRACE=1` prints compiler stack traces, and `LLVMNET_CLANG`, `LLVMNET_FLANG` and `LLVMNET_OPT` select other frontend executables. The driver is not a complete Clang replacement; [docs/toolchain.md](docs/toolchain.md#driver-contract) describes what it accepts.

## Runtime Modes

| Mode | ABI tag | C, C++ and Fortran libraries |
| --- | --- | --- |
| `portable` (default) | `dotnet64-v2` | musl 1.2.5, libc++/libc++abi, LLVM's OpenMP runtime and flang-rt 22.1.8 from `sysroot/dotnet64-v2`, compiled into the program's CIL; system services come from a host layer in `LlvmNet.Runtime` ([docs/dev/host-abi.md](docs/dev/host-abi.md)) |
| `system` | `system-linux-x64-v1` | the host's glibc and other native libraries through generated native bridges; flang-rt built against glibc from `sysroot/system-linux-x64-v1` |

```sh
bin/llvmnet --runtime=portable program.cpp -o program.dll
bin/llvmnet --runtime=system source.c --nativeaot -o program
```

Portable mode uses a fixed little-endian 64-bit ABI with its own headers, independent of the host's, and is meant to run the same image on any 64-bit .NET host. System mode uses the native Linux x86-64 SysV ABI. Every object, archive and managed reference in one program must come from the same mode; the linker rejects mixed or untagged inputs. [docs/toolchain.md](docs/toolchain.md#runtime-modes) defines both contracts, and [ports/musl](ports/musl/README.md) describes the musl port.

## Managed and Native Linking

```sh
bin/llvmnet --emit-library library.c -o Example.dll
bin/llvmnet main.c --cil-reference Example.dll -o managed-client.dll

bin/llvmnet --runtime=system -fno-builtin tests/c/native-import.c \
  --native-import 'abs=libc.so.6!abs' -o native-client.dll
```

`--cil-reference` resolves public static methods marked with `LlvmNet.Runtime.CExportAttribute`, which llvmnet emits on translated functions. Scalar and pointer signatures must match; aggregate signatures across assemblies are not yet a stable ABI.

In system mode, calls to native libraries go through LLVM-generated bridges that support C structures, varargs, native data and scalar callbacks; `--system-library` adds a native library. `--native-import` maps one symbol to an explicit P/Invoke declaration with a non-variadic scalar signature. Portable mode rejects both options. Native C++ library interchange is not supported.

## Deploy

```sh
bash scripts/package-toolchain.sh /path/toolchain --self-contained --bundle-llvm
/path/toolchain/bin/llvmnet-clang source.c -o program.dll
/path/toolchain/bin/llvmnet-clang++ source.cpp -o cpp.dll
/path/toolchain/bin/llvmnet-flang source.f90 -o fortran.dll
/path/toolchain/bin/llvmnet-clang --runtime=system source.c --nativeaot -o program
```

An output name without `.dll` produces a Unix launcher script plus a `<name>.llvmnet/` payload directory. `--nativeaot` produces a native executable that includes the .NET runtime; publishing it needs the .NET 10 SDK and a native linker. Portable images are intended to run on any little-endian 64-bit .NET host; only Linux x86-64 is tested.

## Diagnostics

- Errors carry `file:line:col` when the input has debug information (`-g` or `-gline-tables-only`), the function name and the LLVM instruction. The linked IR is kept as `<output>.failed.bc`. Exit codes: 1 compilation error, 2 usage error, 3 internal compiler error.
- `--diagnose-limits` reports each function's IL size and locals and flags functions the JIT compiles without optimization.
- `--check-indirect-calls` checks every indirect call against its target's signature; see [docs/dev/calling-convention.md](docs/dev/calling-convention.md).
- [docs/user/support-matrix.md](docs/user/support-matrix.md) lists which LLVM types, instructions and intrinsics are translated.

## Tests and Workloads

```sh
tests/run.sh --list                         # every test with its tags and prerequisites
tests/run.sh --tag core                     # compiler and runtime regressions
tests/run.sh --tag core --tag toolchain     # plus packaging and deployment
tests/run.sh --tag integration              # zlib, libxml2, dav1d and ffmpeg from pinned sources
tests/run.sh --tag conformance              # musl libc-test, the libc++ suite, FCVS and gfortran tests
dotnet test tests/LlvmNet.UnitTests -c Release
```

The runner builds once, runs tests in parallel and writes per-test logs, `summary.tsv` and JUnit `results.xml` under `artifacts/tests/results/`. Tests whose prerequisites are missing are reported as skipped. [CONTRIBUTING.md](CONTRIBUTING.md) explains how to add tests.

The ffmpeg profile (`config/ffmpeg-browser.sh`) builds ffmpeg with dav1d, libxml2 and zlib and a decode harness, `tests/ffmpeg/decode.c`:

```sh
LLVMNET_RUNTIME=system bash scripts/build-dependencies.sh
LLVMNET_RUNTIME=system bash scripts/build-ffmpeg.sh
dotnet artifacts/ffmpeg-browser-system/decode.dll input.mp4 10
FFMPEG_TEST_VIDEO=/path/video.mp4 tests/run.sh video
```

`LLVMNET_RUNTIME` is `portable` (default) or `system`; outputs go to separate per-mode directories under `artifacts/`. `FFMPEG_SIMD128=1` builds the HEVC SIMD128 kernels ([docs/simd128.md](docs/simd128.md)). The harness accepts `input [frame-limit] [video|audio] [threads:1-64] [auto|frame|slice] [hash|bench]`; see [docs/ffmpeg-threading.md](docs/ffmpeg-threading.md) and [docs/video-simd128-performance.md](docs/video-simd128-performance.md). `scripts/build-ffmpeg-core.sh` builds a smaller profile.

SPEC CPU 2017 and 2026 run through the ordinary `runcpu` harness with [config/spec/llvmnet.cfg](config/spec/llvmnet.cfg) and [scripts/spec-harness.sh](scripts/spec-harness.sh); see [docs/toolchain.md](docs/toolchain.md#standard-spec-configuration), [docs/spec-coverage.md](docs/spec-coverage.md) and [docs/spec-portable-limitations.md](docs/spec-portable-limitations.md). Licensed kits, generated media and build outputs stay under the ignored `artifacts/` directory.

## Safety

Generated code is unsafe and is not a sandbox: invalid pointers or untrusted programs can corrupt memory or terminate the process. Compile and run only code you trust. Neither mode is a complete libc or POSIX implementation; [docs/status.md](docs/status.md) lists the gaps.

## Documentation

- [docs/toolchain.md](docs/toolchain.md): installation, driver, runtime modes and ABI contracts, SPEC configuration.
- [docs/architecture.md](docs/architecture.md): how LLVM IR becomes CIL.
- [docs/status.md](docs/status.md): current coverage and limitations.
- [docs/plan/README.md](docs/plan/README.md): production-readiness plan.
- [docs/dev/](docs/dev/): host interface, calling convention, C++ exception bridge, versioning, artifact layout.

## License

llvmnet is licensed under the MIT License; see [LICENSE.TXT](LICENSE.TXT). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Contributions are accepted under the same license; see [CONTRIBUTING.md](CONTRIBUTING.md).
