# Getting started

llvmnet compiles C, C++ and Fortran to .NET: Clang or Flang 22 produces LLVM bitcode, llvmnet links it and translates it to CIL, and the result runs as ordinary .NET code, under the JIT or as a NativeAOT executable. The compiler runs on Linux x86-64; the programs it produces run on Linux x64 and ARM64, Windows x64 and macOS ARM64 ([deployment.md](deployment.md#supported-hosts)).

## Install

Pick one of three ways to get a toolchain.

**A release package** (Linux x86-64). Each release ships two archives: `llvmnet-<version>-linux-x64.tar.xz` needs LLVM, Clang and Flang 22 installed (`clang-22`, `flang-22`, `opt-22`, `llvm-ar-22`, `llvm-nm-22`, `llvm-ranlib-22` on `PATH`), and `llvmnet-<version>-linux-x64-llvm.tar.xz` bundles them. Neither needs `dotnet` to compile; running the programs needs the .NET 10 runtime, and NativeAOT publishing needs the .NET 10 SDK and a native linker. [deployment.md](deployment.md#release-packages) says how to verify the download.

```sh
tar xJf llvmnet-0.3.0-linux-x64-llvm.tar.xz
export PATH="$PWD/llvmnet-0.3.0-linux-x64-llvm/bin:$PATH"
```

**The dotnet tool** (Linux x86-64, LLVM 22 on `PATH`): `dotnet tool install -g llvmnet` from the `llvmnet.<version>.nupkg` attached to a release (`--add-source <directory>`), which gives the `llvmnet` command; `--driver-mode=g++` and `--driver-mode=flang` select the C++ and Fortran frontends.

**From source** (Debian 13 or a compatible Linux): the .NET 10 SDK, LLVM 22 with development files (`scripts/ci/install-packages.sh` installs the whole set as root) and network access for the pinned sources of the portable sysroot.

```sh
git clone <the llvmnet repository> && cd llvmnet
bash scripts/ensure-built.sh        # the solution and the sysroots (several minutes the first time)
bin/llvmnet --version
```

`bin/llvmnet`, `bin/llvmnet-clang`, `bin/llvmnet-clang++` and `bin/llvmnet-flang` then work from the checkout, and `scripts/package-toolchain.sh <prefix> [--self-contained] [--bundle-llvm]` installs a relocatable copy.

## A first program

```sh
cat >hello.c <<'C'
#include <stdio.h>
int main(int argc, char **argv) { printf("hello from %s\n", argc > 1 ? argv[1] : "llvmnet"); return 0; }
C
llvmnet-clang hello.c -o hello.dll
dotnet hello.dll C
```

`hello.dll` is a .NET assembly; the compiler writes `hello.runtimeconfig.json` and a copy of `LlvmNet.Runtime.dll` next to it, and the three files together are the program. The same works for C++ and Fortran:

```sh
llvmnet-clang++ -std=c++20 -O2 app.cpp -o app.dll
llvmnet-flang -O2 solver.f90 -o solver.dll
```

An output name without `.dll` gives a Unix launcher script plus a `<name>.llvmnet/` directory, so `./app` runs it; `--nativeaot -o app` gives a native executable that needs no `dotnet` at all ([deployment.md](deployment.md)).

## Build systems

The command line follows Clang's ([driver.md](driver.md)), so a Makefile works with `CC=llvmnet-clang CXX=llvmnet-clang++ FC=llvmnet-flang`, and objects and archives are LLVM bitcode:

```sh
llvmnet-clang -O2 -c a.c -o a.o
llvmnet-clang -O2 -c b.c -o b.o
llvmnet-ar rcs libab.a a.o b.o
llvmnet-clang main.c -L. -lab -o program.dll
```

For CMake, `cmake -DCMAKE_TOOLCHAIN_FILE=<toolchain>/share/llvmnet/cmake/llvmnet.cmake` (in a checkout, `cmake/llvmnet.cmake`); for Meson, `meson setup --cross-file <toolchain>/share/llvmnet/meson/llvmnet.ini`. Both run the produced executables for configure checks, so a configured project builds and runs its tests as usual. `scripts/build-dependencies.sh` builds zlib, libxml2 and dav1d this way, and the ffmpeg profile on top of them ([dev/ffmpeg.md](../dev/ffmpeg.md)).

## Two runtime modes

| Mode | What it is | Use it for |
| --- | --- | --- |
| `--runtime=portable` (default) | A fixed 64-bit ABI with musl, libc++ and flang-rt compiled into the program; the operating system is reached through a host layer in `LlvmNet.Runtime`, so one image runs on every supported host | Programs that should run anywhere .NET runs, and libraries consumed from .NET |
| `--runtime=system` | The native Linux x86-64 ABI: the host's glibc and other native libraries through generated bridges | Programs that must call native libraries, on Linux x64 only |

[modes.md](modes.md) defines both contracts; every object, archive and managed reference in one program must come from the same mode.

## Next

- [driver.md](driver.md): every option, with what the driver does with it.
- [linking.md](linking.md): bitcode archives, managed libraries (`-shared`, `--cil-reference`) and native libraries (`--system-library`).
- [deployment.md](deployment.md): what to ship, NativeAOT, other hosts, release packages.
- [limitations.md](limitations.md) and [troubleshooting.md](troubleshooting.md): what does not work, and what an error means.
