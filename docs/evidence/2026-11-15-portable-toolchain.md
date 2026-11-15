# Evidence: the portable toolchain on Linux, macOS and Windows (XP-04, XP-09; 2026-11-15)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang/Flang 22.1.8 (Debian), branch `portable-toolchain` from master `ac2ac17` with TC-21 merged. The host also ran another project's builds throughout, so Linux timings carry noise. Test hosts: macOS 27.2.1 ARM64 (`mainmac`, .NET runtime 10.0.14 in a private directory, not on `PATH`) and Windows 11 x64 (`fluorite`, .NET runtimes 10.0.15 and 11.0.0, Git for Windows, Visual Studio 2026 with its CMake and Ninja).

## What was built

The owner asked for the Clang/LLVM stack built with llvmnet (`artifacts/selfhost/`, 2026-11-02) to run on Windows and macOS from the same build, published as a portable toolchain that needs only the .NET 10 runtime.

- **The compiler's LLVM.** `scripts/selfhost/` configures LLVM, Clang, MLIR and Flang 22.1.8 for portable mode in one tree: `-fvisibility=hidden` with C API export annotations, `__linux__` and friends (ADR-14), one source patch (`getStackPointer`). `build-library.sh` links the 52 component libraries the bridge needs (1,562 objects), `native/*.cpp` and a roots table from `llvmnet --print-llvm-imports` with the new `-shared --embed-libc` into `LlvmNet.Llvm.dll` (27 MB). `LlvmManaged` binds the compiler's 144 imports to its exports after checking each signature.
- **The tools.** `build-tools.sh` builds `clang`, `flang`, `opt`, `llvm-ar` and `llvm-nm` as portable JIT images (196, 287, 96, 16 and 19 MB) and stages them with a POSIX and a `.cmd` launcher per name, Clang's resource headers and Flang's 14 intrinsic modules, which the built flang makes byte-identical to Debian's.
- **The archives.** `release-artifacts.sh` with the `portable` and `portable-linux-x64` variants: the universal toolchain as a 97 MB `.tar.xz` and a 176 MB `.zip`, the `linux-x64` one as a 261 MB `.tar.xz`, none with a symbolic link.
- **The package.** `package-toolchain.sh --portable` (733 MB): the framework-dependent compiler without an apphost, the library, the tools, the sysroots, and `bin/` commands for Unix (bash 3.2) and Windows. `--readytorun=<rid>` precompiles every assembly with crossgen2 (2 minutes; deterministic: two runs give identical files), which grows it to 1.1 GB (`win-x64`), 1.2 GB (`linux-x64`) and 1.3 GB (`osx-arm64`); the `win-x64` zip is 389 MB.

## The compiler on the managed LLVM

On Linux, with Debian's Clang and `opt` in both cases, `LLVMNET_LLVM=native` against `LlvmNet.Llvm.dll`:

| | native `libLLVM-22` | `LlvmNet.Llvm.dll` (JIT) |
| --- | ---: | ---: |
| `hello.c -O2` to an image | 0.67–0.71 s | 2.22–2.24 s |
| the image | | byte-identical |

The core gate with the compiler forced onto the managed LLVM (`LLVMNET_LLVM=<library> tests/run.sh --tag core --exclude-tag slow`): 108 of the 109 tests that ran passed; the one failure was `driver-reference` regenerating `docs/user/driver.md` for the new `--embed-libc` option. The run included a full rebuild of the sysroot through the managed LLVM (1,336 musl, 68 libc++, 31 libomp and 134 flang-rt sources). `jit-validation`, which the run did not reach, passed on its own with the managed LLVM. The pull-request gate on the native LLVM (`tests/run.sh --tag core --tag toolchain --exclude-tag slow`) passed 122 of 122 at the end, and the unit tests 161 of 161.

## The kit on three hosts

`tests/portable-toolchain/make-kit.sh` collects the sources of the 59 cross-platform fixtures (`tests/xp/fixtures.tsv`: 33 C, 13 C++, 13 Fortran) with the output and status of their native Linux builds; the kit's `run.sh` compiles every fixture on the host with the toolchain under test, runs it and compares, then checks a launcher output (`argv[0]` and exit status), separate compilation with `llvmnet-ar`, `llvmnet-ranlib` and `llvmnet-nm`, Fortran modules across files, and on Windows the `.cmd` commands.

| Host | Package | Result | Wall time |
| --- | --- | --- | ---: |
| Linux x64 | universal (JIT), before the release run | 62 of 62 | 206 s (6 jobs, with other builds running) |
| Linux x64 | `linux-x64` from the release archive | 62 of 62 | 43 s (8 jobs) |
| macOS ARM64 | `osx-arm64` | 62 of 62 | 37 s (6 jobs) |
| Windows x64 | `win-x64`, unpacked from the zip with Windows' `tar` | 63 of 63 | 74 s (6 jobs) |

The Linux and Windows rows after the `.cmd` exit-status fix also run each launcher through `llvmnet --run-launcher`. Builds: the Linux archive is `dc0791c`'s tree; Windows ran the `win-x64` package of `e2045d1` with `llvmnet.dll` and the CMake file of `dc0791c` put in; macOS ran the `osx-arm64` package of `aa98f0d`, whose later changes concern Windows only.

Compile and link of a hello program, best of two:

| Host, package | C | C++ (`<iostream>`) | Fortran |
| --- | ---: | ---: | ---: |
| Linux x64, universal release archive (JIT) | 6.6 s | 12.3 s | 11.1 s |
| Linux x64, `linux-x64` release archive | 1.9 s | 4.0 s | 3.7 s |
| macOS ARM64, `osx-arm64` | 1.0 s | | |
| Windows x64, `win-x64` (PowerShell) | 2.3 s | 4.8 s | 4.2 s |

The Linux host had a load average of 14 from other work. The difference is the JIT start-up of the large images, which `-c` alone shows with the experiment's images staged the same way (C 7.5 s against 1.4 s, C++ 17.9 s against 3.9 s).

**Identical output.** `core.c -O2` and `streams.cpp -O1 -std=c++17`, each compiled to the same output name, give the same SHA-256 from the native-LLVM compiler on Linux and from the portable toolchain on Linux, macOS and Windows (`8c1f164f…` and `df63fe29…`): the llvmnet-built Clang emits the same IR as Debian's, apart from the `llvm.ident` string, which does not reach the image.

## Existing libraries

`tests/portable-toolchain/libraries.sh` builds the pinned zlib 1.3.2 with CMake (its example tests through `ctest`, a `minigzip` round trip), libxml2 2.15.3 with CMake against that zlib (`xmllint --version`, an XPath query) and dav1d 1.5.4 with Meson (`dav1d --version`):

- **macOS ARM64**, `osx-arm64` package, CMake, Ninja and Meson from Homebrew: all three in 68 s.
- **Windows x64**, `win-x64` package, Visual Studio's CMake and Ninja from PowerShell: zlib (configure, build, 2 of 2 tests, round trip) and libxml2 (configure, build, `xmllint`). Meson was not tried.
- **Linux x64**, the `linux-x64` release archive: all three in 92 s (the native-LLVM package: 32 s).

## Found and fixed on the way

- **Windows' main stack.** dotnet.exe gives the main thread 1.5 MB; the precompiled type initializer of the llvmnet-built clang needs about 2 MB (reproduced on Linux with `ulimit -s 1536`: the JIT-compiled one starts in 1 MB, flang's needs 2 MB, opt's less than 1 MB). Portable entry points now run on an 8 MB thread on Windows, as `RLIMIT_STACK` promises.
- **Flang's `-fc1` on Windows.** The driver keeps a program path only when `access(X_OK)` succeeds, and the host layer reported no file executable on Windows, so it spawned a bare `flang`. `stat` and `access` now give 0755 to `.exe`, `.com`, `.bat`, `.cmd` and extensionless `#!` files there.
- **Exit status through `.cmd`.** `cmd /c launcher.cmd` reported 0 after a bare `exit /b`; launchers pass `%ERRORLEVEL%`.
- **CMake on Windows.** `cmd.exe /c` cannot run CMake's forward-slash paths; probes, `try_run` and tests run through the hidden `llvmnet --run-launcher`.
- **Meson.** The cross file named the host's `llvm-ar-22`; it uses `llvmnet-ar`.
- **A frontend start per compile.** The driver ran `clang -print-resource-dir` at every portable compile; the resource directory is found beside the frontend.
- **Cached LLVM builds.** A sysroot rebuild gives unchanged headers new times, which made ninja recompile all of LLVM; the selfhost scripts restore the time recorded with the header hash.

## The dotnet requirement

- No `dotnet` anywhere (a `debian:trixie` container without .NET): `llvmnet: the .NET 10 runtime is required, and no dotnet was found on PATH, in DOTNET_ROOT or in the usual install locations.` and the install hint, exit 127; on Windows (the install location pointed elsewhere) the same with `winget install Microsoft.DotNet.Runtime.10`, exit 9009.
- A `dotnet` with only .NET 8 (`DOTNET_ROOT` at such a tree on Windows): `llvmnet: the .NET 10 runtime (Microsoft.NETCore.App 10.x) is required; "...\dotnet.exe" does not have it.`, exit 150.
- macOS with .NET only in a private directory: found through `DOTNET_ROOT`, or through the usual locations when one of them has it.

## Commands

```sh
bash scripts/selfhost/configure.sh && bash scripts/selfhost/build-library.sh && bash scripts/selfhost/build-tools.sh
bash scripts/package-toolchain.sh artifacts/portable/universal --portable
bash scripts/package-toolchain.sh artifacts/portable/win-x64 --portable --readytorun=win-x64     # and osx-arm64, linux-x64
bash tests/portable-toolchain/make-kit.sh                                                         # artifacts/tests/portable-toolchain/kit
bash kit/run.sh --jobs 6 kit <toolchain>                                                          # on each host
bash tests/portable-toolchain/libraries.sh <toolchain> <work> [<sources>]
LLVMNET_LLVM=artifacts/selfhost-build/library/LlvmNet.Llvm.dll tests/run.sh --tag core --exclude-tag slow
```
