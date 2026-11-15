# llvmnet

llvmnet is a C, C++ and Fortran compiler whose output is .NET. Point a build at `llvmnet-clang`, `llvmnet-clang++` and `llvmnet-flang` instead of `cc`, `c++` and `gfortran`, and the programs and libraries it produces are .NET assemblies that run unchanged on Linux, Windows and macOS, x64 and ARM64, wherever the .NET 10 runtime is installed. The C library, the C++ standard library and the Fortran runtime (musl, libc++, flang-rt) are compiled into each program, so it sees the POSIX system it was written for on every host.

The compiler itself is portable too. Clang, Flang and the LLVM libraries the compiler uses are built with llvmnet, so the whole toolchain is .NET code: one package runs on any .NET 10 host, with nothing else to install.

llvmnet is at version 0.x and not yet production-ready. What it does today is checked by tests and recorded with dates and commands under [docs/evidence/](docs/evidence/). The upstream conformance suites of musl, libc++ and Flang pass at their documented thresholds, ffmpeg decodes media identically to native builds, and every SPEC CPU 2017 and 2026 entry passes or is a documented exclusion. [docs/user/limitations.md](docs/user/limitations.md) lists what does not work.

## Install

**The portable toolchain** (Windows, macOS and Linux; x64 and ARM64). It needs only the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Each release has a universal package and one precompiled for each common host. They contain the same toolchain, used the same way: the precompiled one has Clang, Flang and the LLVM tools as native executables for its host, so it compiles and links a small C program in about a second instead of about seven, and runs on that host only.

| Host | Package |
| --- | --- |
| Windows x64 | `llvmnet-<version>-portable-win-x64.zip` |
| Windows ARM64 | `llvmnet-<version>-portable-win-arm64.zip` (built, not yet tested on such a host) |
| macOS ARM64 | `llvmnet-<version>-portable-osx-arm64.tar.xz` |
| Linux x64 | `llvmnet-<version>-portable-linux-x64.tar.xz` |
| Linux ARM64 | `llvmnet-<version>-portable-linux-arm64.tar.xz` |
| any other .NET 10 host | `llvmnet-<version>-portable.tar.xz` or `.zip` |

Unpack it anywhere and put its `bin` directory on `PATH`:

```sh
tar xJf llvmnet-0.3.0-portable-osx-arm64.tar.xz
export PATH="$PWD/llvmnet-0.3.0-portable-osx-arm64/bin:$PATH"
llvmnet-clang --version
```

```powershell
# Windows (PowerShell; in cmd.exe: set PATH=%CD%\llvmnet-0.3.0-portable-win-x64\bin;%PATH%)
tar -xf llvmnet-0.3.0-portable-win-x64.zip
$env:PATH = "$PWD\llvmnet-0.3.0-portable-win-x64\bin;$env:PATH"
llvmnet-clang --version
```

The commands find `dotnet` on `PATH`, in `DOTNET_ROOT` or in its usual install location. When none is there, or it has no .NET 10 runtime, they say so and how to install it.

**Linux x86-64 packages with native LLVM.** These compile faster on Linux, because Clang and LLVM run as native code. `llvmnet-<version>-linux-x64-llvm.tar.xz` bundles LLVM 22, and `llvmnet-<version>-linux-x64.tar.xz` uses the LLVM 22 on `PATH`. **From source** (Debian 13 or compatible): install the .NET 10 SDK and LLVM 22 (`scripts/ci/install-packages.sh`), then run `bash scripts/ensure-built.sh`. [docs/user/getting-started.md](docs/user/getting-started.md) covers each way to install.

## Compile a program

```sh
llvmnet-clang hello.c -o hello.dll       # a .NET assembly, its runtimeconfig.json and LlvmNet.Runtime.dll
dotnet hello.dll

llvmnet-clang++ -std=c++20 -O2 app.cpp -o app        # a launcher, plus the assembly in app.llvmnet/
./app                                                # app.cmd on Windows

llvmnet-flang -O2 solver.f90 -o solver.dll
```

The output runs on every supported host. Copy `hello.dll` with the two files beside it, or the launcher with its `.llvmnet` directory, to a Windows, macOS or Linux machine and run it there. The launcher is a shell script; a compile on Windows also writes `app.cmd`. The toolchain gives byte-identical assemblies on Linux, Windows and macOS from the same sources.

| You want | Use | What you get |
| --- | --- | --- |
| a program that runs on any .NET host | `-o name.dll` or `-o name` | a CIL assembly (and a launcher) |
| a library for C# and other .NET code | `-shared -o Name.dll` (with `--embed-libc` when it uses the C library) | a CIL library whose C functions are public methods |
| a native executable | `--nativeaot -o name` | a self-contained executable for one host (needs the .NET 10 SDK and a native linker) |
| bitcode objects and archives | `-c`, `llvmnet-ar` | LLVM bitcode, linked into one of the above |

## Build existing projects

The command line follows Clang's, and objects and archives are LLVM bitcode. Builds that take `CC`, `CXX` and `FC` work unchanged:

```sh
make CC=llvmnet-clang CXX=llvmnet-clang++ FC=llvmnet-flang AR=llvmnet-ar RANLIB=llvmnet-ranlib
./configure CC=llvmnet-clang CXX=llvmnet-clang++ AR=llvmnet-ar RANLIB=llvmnet-ranlib NM=llvmnet-nm
```

CMake and Meson use the files the toolchain ships in `share/llvmnet/`. Configure checks link and run real programs, so a project configures as it would with a native compiler. These build three libraries from their release tarballs with their own build systems, and run what they build (`tests/portable-toolchain/libraries.sh`):

```sh
TOOLCHAIN=/path/to/llvmnet-0.3.0-portable-osx-arm64

# zlib, with CMake, and its tests
cmake -G Ninja -S zlib-1.3.2 -B build/zlib -DCMAKE_TOOLCHAIN_FILE=$TOOLCHAIN/share/llvmnet/cmake/llvmnet.cmake \
      -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=$PWD/prefix -DZLIB_BUILD_SHARED=OFF
cmake --build build/zlib --target zlibstatic zlib_static_example static_minigzip
(cd build/zlib && ctest -R zlib_static_example)
cmake --install build/zlib

# libxml2, with CMake, against that zlib
cmake -G Ninja -S libxml2-2.15.3 -B build/libxml2 -DCMAKE_TOOLCHAIN_FILE=$TOOLCHAIN/share/llvmnet/cmake/llvmnet.cmake \
      -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH=$PWD/prefix -DBUILD_SHARED_LIBS=OFF -DLIBXML2_WITH_ZLIB=ON \
      -DLIBXML2_WITH_PYTHON=OFF -DLIBXML2_WITH_ICONV=OFF
cmake --build build/libxml2
build/libxml2/xmllint --version

# dav1d, with Meson
meson setup build/dav1d dav1d-1.5.4 --cross-file $TOOLCHAIN/share/llvmnet/meson/llvmnet.ini \
      --buildtype release --default-library static -Denable_asm=false
meson compile -C build/dav1d
build/dav1d/tools/dav1d --version
```

These run as shown on macOS and Linux; on Windows, zlib and libxml2 build the same way from PowerShell with Visual Studio's CMake and Ninja, where the toolchain file uses the `.cmd` commands. `xmllint` and `dav1d` here are launchers: the same files run on Windows, macOS and Linux. A few things differ from a native build:

- **Libraries are archives.** `SHARED` libraries become archives of bitcode, linked into each program as a whole.
- **No assembly.** Assembly sources and SIMD paths written in assembly are switched off, as `-Denable_asm=false` does above.
- **Some flags are rejected or ignored.** Flags that need a native runtime, such as `-coverage` and `-fsanitize=address`, are rejected. Native linker flags are ignored or rejected, as [docs/user/driver.md](docs/user/driver.md) lists per option.

[docs/user/linking.md](docs/user/linking.md#build-system-integration) describes the CMake platform, configure probes and the autotools recipe; `scripts/build-dependencies.sh` and [docs/dev/ffmpeg.md](docs/dev/ffmpeg.md) build ffmpeg the same way.

## Call C from .NET

`-shared` makes a CIL library whose default-visibility C functions are public static methods of a class `Program`. With `--embed-libc`, the library carries its own C library and can be called from any .NET code without a program around it:

```c
// numbers.c
#include <stdio.h>
__attribute__((visibility("default"))) int gcd(int a, int b) { while (b) { int t = a % b; a = b; b = t; } return a; }
__attribute__((visibility("default"))) void report(int value) { printf("C says %d\n", value); fflush(stdout); }
```

```sh
llvmnet-clang -O2 -shared --embed-libc -fvisibility=hidden numbers.c -o Numbers.dll
```

```csharp
var program = Assembly.LoadFrom("Numbers.dll").GetType("Program")!;
var gcd = (int)program.GetMethod("gcd")!.Invoke(null, [84, 36])!;
program.GetMethod("report")!.Invoke(null, [gcd]);    // C says 12
```

[docs/user/linking.md](docs/user/linking.md#managed-libraries) gives the rules. The compiler itself calls LLVM this way.

## Two runtime modes

| Mode | ABI | Runs on | Libraries |
| --- | --- | --- | --- |
| `--runtime=portable` (default) | `dotnet64-v2`, fixed and independent of the host | every supported .NET host, JIT or NativeAOT | musl, libc++, LLVM's OpenMP runtime and flang-rt compiled into the program; system services from `LlvmNet.Runtime`; host libraries such as EGL and OpenGL ES through [native extensions](docs/user/extensions.md) |
| `--runtime=system` | `system-linux-x64-v1`, the native Linux x86-64 ABI | Linux x64 | the host's glibc and native libraries through generated bridges (needs a Linux toolchain with native LLVM) |

[docs/user/modes.md](docs/user/modes.md) defines both contracts. Generated code is unsafe and is not a sandbox: compile and run only code you trust.

## Documentation

For users, in [docs/user/](docs/user/): [getting started](docs/user/getting-started.md), the [driver reference](docs/user/driver.md), [runtime modes and ABI contracts](docs/user/modes.md), the frozen [portable ABI](docs/user/abi.md), [linking](docs/user/linking.md) bitcode, managed and native libraries, [native extensions](docs/user/extensions.md), [deployment](docs/user/deployment.md) (what to ship, the portable toolchain, NativeAOT, hosts, release packages), [limitations](docs/user/limitations.md), [troubleshooting](docs/user/troubleshooting.md), the [LLVM IR support matrix](docs/user/support-matrix.md), [SIMD and vector code](docs/user/simd.md) with the [SIMD128](docs/user/simd128.md) path, [x86 intrinsics](docs/user/x86-intrinsics.md) and [native kernel packages](docs/user/native-kernels.md), and the [support and deprecation policy](docs/user/support.md). [SECURITY.md](SECURITY.md) says what llvmnet does and does not protect against and how to report a vulnerability.

For contributors, in [docs/dev/](docs/dev/): the [architecture](docs/dev/architecture.md), the [host interface](docs/dev/host-abi.md) of portable programs, [calls and signatures](docs/dev/calling-convention.md), the [C++ exception bridge](docs/dev/cxx-eh-bridge.md), [versioning](docs/dev/versioning.md), [testing](docs/dev/testing.md), [adding a host function](docs/dev/adding-a-host-function.md), [writing a native extension](docs/dev/extensions.md), the [release process](docs/dev/release-process.md), the [gates](docs/dev/gates.md), the [artifacts directory](docs/dev/artifacts.md), the [SPEC CPU](docs/dev/spec.md) and [ffmpeg](docs/dev/ffmpeg.md) workloads and the [video benchmark](docs/dev/video-benchmark.md). [scripts/selfhost/README.md](scripts/selfhost/README.md) builds LLVM with llvmnet for the portable toolchain. [CONTRIBUTING.md](CONTRIBUTING.md) says how to build, run the gates and add a test.

Evidence, dated and produced by commands in the repository, is in [docs/evidence/](docs/evidence/); the production-readiness plan, its decisions and the status board are in [docs/plan/](docs/plan/README.md).

## Tests

```sh
tests/run.sh --tag core --tag toolchain --exclude-tag slow   # the pull-request gate
dotnet test tests/LlvmNet.UnitTests -c Release
tests/run.sh --list                                          # every test with its tags and prerequisites
```

The runner builds once, runs the tests in parallel and writes per-test logs, `summary.tsv` and JUnit `results.xml` under `artifacts/tests/results/`. [docs/dev/testing.md](docs/dev/testing.md) describes the tags, the oracles and the nightly suites. `tests/portable-toolchain/` checks a portable toolchain on any host: `make-kit.sh` collects the cross-platform fixtures with their native results, and the kit's `run.sh` compiles and runs them there.

## License

llvmnet is licensed under the MIT License; see [LICENSE.TXT](LICENSE.TXT). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Contributions are accepted under the same license; see [CONTRIBUTING.md](CONTRIBUTING.md).
