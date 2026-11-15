# Evidence: releases bootstrapped from the previous release, and NativeAOT tools in the precompiled packages (XP-11, XP-14; 2026-11-16)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.404, LLVM/Clang/Flang 22.1.8 (Debian), branch `release-bootstrap` from master `1715df6`. Another project's builds ran on the host throughout, and the bootstrap build overlapped the NativeAOT publishes, so Linux wall times carry noise; CPU times of single compiles were taken with the host lightly loaded. Test hosts as for XP-04 ([2026-11-15](2026-11-15-portable-toolchain.md)): macOS ARM64 (`mainmac`, 12 cores, 32 GB, .NET SDK 10.0.403 in a private directory) and Windows 11 x64 (`fluorite`, .NET SDK 10.0.404, Visual Studio 2026 with MSVC, Git for Windows).

## What was asked

The owner asked for the release pipeline to build LLVM with the published toolchain [v0.3.0-dev.3](https://github.com/hjc4869/llvmnet/releases/tag/v0.3.0-dev.3) (built by the release workflow from `1715df6`) instead of the runner's native LLVM, and for the precompiled per-host packages to use NativeAOT instead of ReadyToRun, used the same way as the universal package.

## The bootstrap build (XP-11)

**The pin.** `llvmnet-bootstrap` in `scripts/sources.lock` is the release's universal package (`llvmnet-0.3.0-dev+1715df60616b-portable.tar.xz`, 97 MB, SHA-256 `0aea205a…`, the value in the release's `SHA256SUMS`). `scripts/selfhost/bootstrap.sh` unpacks it and precompiles it for the host with `scripts/precompile-toolchain.sh` (the tools as NativeAOT executables, the compiler with ReadyToRun) in `<work>/bootstrap`, which `LLVMNET_SELFHOST_TOOLCHAIN` names: 14 min 14 s on 4 cores (5.6 GiB peak), and nothing when the directory already holds the pin. The bootstrap run below used the release's `linux-x64` package, whose tools are ReadyToRun images, before the pin moved to the universal package for the reason under *Costs*; objects from the prepared toolchain are byte-identical to theirs (6 of 6 sampled across LLVM, Clang, MLIR and Flang, and a native TableGen object of the NATIVE build).

**Who does what.**

- The release's `llvmnet-clang++` compiles LLVM, Clang, MLIR and Flang, with its own Clang, `opt` and sysroot headers.
- LLVM's NATIVE sub-build makes every host tool of the build (`llvm-min-tblgen`, `llvm-tblgen`, `clang-tblgen`, `mlir-tblgen`, `mlir-linalg-ods-yaml-gen`; 535 objects) with the release's `clang-22` as an ordinary native compiler: an llvmnet-built Clang emits x86-64 code and links with the system's `ld` and libstdc++. Before, TableGen was Debian's and the NATIVE build used `clang-22`.
- `build-library.sh` links `LlvmNet.Llvm.dll` twice: first with the release's compiler (`stage1/`), then with this checkout's compiler running on that first library and on the release's Clang, Flang and `opt` (`LLVMNET_LLVM`, `LLVMNET_CLANG`, `LLVMNET_FLANG`, `LLVMNET_OPT`). The bridge in `native/` changes several times a week, so this checkout's compiler cannot load the release's own library; the first link gives it one with this checkout's bridge.
- `build-tools.sh` has ninja build each tool's inputs and runs ninja's own link command with this checkout's driver in place of the release's, so the tools match the runtime they ship with.
- `llvm-config` is gone from the scripts: a cross build's NATIVE `llvm-config` reports the libraries of the main tree as missing (`llvm-config: error: missing: …/NATIVE/lib/libLLVMBitReader.a`), and with an empty list ninja built the whole tree. `build-library.sh` takes the libraries from the tree's `lib/cmake/llvm/LLVMExports.cmake` instead, each before the ones it needs: the same 52 libraries `llvm-config-22 --link-static --libs core bitreader bitwriter irreader linker object passes codegen` lists, in both trees.

**Same output as the native build.** The native-LLVM tree (`artifacts/selfhost-build`, compiled by this checkout's compiler on Debian's Clang and TableGen) was relinked with the same compiler and scripts (`run-native.sh`), and compared:

| Output | Bootstrap (release's LLVM) against native (Debian's LLVM) |
| --- | --- |
| `LlvmNet.Llvm.dll` | byte-identical (`1e168708…`) |
| `clang-22`, `opt`, `llvm-ar`, `llvm-nm` images | byte-identical |
| `flang-22` image | differs only in the 1,268 source paths of Flang's `CHECK` messages (`__FILE__`), the two trees being in different directories: 21 of 21 sampled objects (Flang, MLIR, Clang's Sema, LLVM IR) have identical IR with the directories and `llvm.ident` masked |
| Flang's 14 intrinsic modules, Clang's resource headers | byte-identical |

And with the compiler of the release itself: the first library, which the release links from objects the release compiled, is byte-identical to the release's own `LlvmNet.Llvm.dll` (`c481e389…`), which the release workflow built from objects Debian's Clang compiled.

**What the output depends on.** The final library is deterministic: relinked by this checkout's compiler on the first library and on Debian's native LLVM, it is the same file. It differs from the release's because the compiler that translates it was built here, not in the workflow: the unmodified `1715df6` compiler built on this host gives this branch's bytes, and so does the release's own `llvmnet.dll` given a `LlvmNet.Runtime.dll` built on this host. The translated image follows the metadata order of the runtime assembly it references, which a different SDK lays out differently. Every package ships the runtime its images were translated against, so this is no fault, but byte comparisons across builds need the same runtime build.

**Costs.** One object (`lib/IR/Instructions.cpp.o`, `-O3`), CPU time, the host lightly loaded:

| Compiling toolchain | Wall | CPU (user) |
| --- | ---: | ---: |
| this checkout on Debian's LLVM | 2.7 s | 2.6 s |
| the release's `linux-x64` package (ReadyToRun tools) | 6.4–6.9 s | 11.8–12.7 s |
| the same with `DOTNET_TieredCompilation=0` | 8.2 s | 7.9 s |
| the release's universal package precompiled here (NativeAOT tools) | 3.7 s | 3.4–3.6 s |

The ReadyToRun tools spend most of their time recompiling hot methods at tier 1 on background threads, which on a 4-core runner would make a cold build several times longer than the native one; with NativeAOT tools a bootstrap costs about 1.3 times the native build. Hence the universal pin and `bootstrap.sh`, which works for every release; releases from this change on would also do as `linux-x64` packages. The local bootstrap run (ReadyToRun tools, 20 jobs on an oversubscribed host): 3,526 objects at 22.3 s mean wall time (the native tree's 3,541 took 7.6 s), the library step 3.5 minutes once its objects were built (both links), the tools step 38 minutes including its last 1,040 build steps, with a 12.1 GiB peak (a tool's link on the managed in-process LLVM; 8.7 GiB in the native tree, on Debian's).

## NativeAOT tools in the precompiled packages (XP-14)

**The compiler stays ReadyToRun.** A NativeAOT publish of the compiler with `LlvmNet.Llvm.dll` compiled in (bound through its `Program` methods by field name, since NativeAOT does not return `DllImportAttribute` for the imports) takes 20 s and gives a 26 MB executable, which fails at its first compile: `PlatformNotSupportedException: Dynamic code generation is not supported on this platform` from `ReflectionEmitThrower`, because `PersistedAssemblyBuilder` is not available under NativeAOT. Recorded as XP-15.

**The tools.** `scripts/aot-tools.sh projects` writes each tool image's NativeAOT project with the new hidden `llvmnet --write-aot-project=<image> -o <dir>` (the project `--aot-project` writes for a link); `aot-tools.sh publish` publishes them for a runtime identifier without stack-trace data (Flang's metadata would exceed ILC's 32 MB blob) and names them `clang-22`, `flang-22`, `opt-22`, `llvm-ar-22`, `llvm-nm-22`. `precompile-toolchain.sh` replaces the payloads and launchers in `libexec/llvm/bin` with them and, last, precompiles the compiler, the library and the runtime with ReadyToRun (an image precompiled for Windows does not load on Linux, so the Windows launchers are written first).

| Publish of the five tools | Time | Peak memory |
| --- | ---: | ---: |
| Linux x64, clang alone on 4 cores | 6 min 12 s | 4.7 GiB |
| Linux x64, all five, 8 cores beside other builds | 11 min 19 s | 4.9 GiB (an earlier run) |
| macOS ARM64, all five, 12 cores | 5 min 39 s | 6.3 GiB |
| Windows x64, all five | 5 min 23 s | |

Sizes (Linux): `clang-22` 118 MB, `flang-22` 144 MB, `opt-22` 62 MB, `llvm-ar-22` 14 MB, `llvm-nm-22` 15 MB. The `linux-x64` package is 478 MB unpacked and 116 MB as `.tar.xz` (ReadyToRun: 1.2 GB and 261 MB), `osx-arm64` 101 MB (253 MB), the `win-x64` zip 174 MB (389 MB). The Linux executables need glibc 2.34 (`objdump -T`: `GLIBC_2.34` at most), the macOS ones carry the linker's ad-hoc signature.

**Found and fixed on the way.**

- On Windows the compiler gave a NativeAOT tool host paths (`C:\…\input0.bc`), which the portable Clang cannot open: it converted paths to the program's form only for launcher payloads. `Toolchain.Start` now does so for the toolchain's own tools in `libexec/llvm/bin`, which are llvmnet-built on every Windows host.
- Flang's driver runs its frontend as `flang -fc1`, looking for a file named `flang` beside itself; on Windows there was only `flang-22.exe`, so it fell back to `flang` on `PATH` (`posix_spawn failed: No such file or directory`). A launcher whose payload is not a `.dll` now names a native executable, which `Launchers.Start` (the host layer, the compiler) and the launcher scripts run directly, and the Windows package has the other names (`flang`, `clang++`, `llvm-ranlib-22`, …) as launcher pairs to the `.exe`. Launchers of images are written byte for byte as before. The tools' runtime is compiled into them, so the executables must be published from payloads that carry this runtime, as the release's are.
- C++ frontend queries ran `clang++-22`; they now run `clang-22 --driver-mode=g++`, as compilations already did. `Toolchain.Executable` finds `<name>.exe` on Windows, and `llvmnet-ar.cmd` and the other tool commands run a tool's `.exe` without looking for `dotnet`.

**The kit on three hosts.** The release flow end to end on the bootstrapped outputs: the universal package from the bootstrap build, its tools' projects, published on each host, the packages made on Linux by `release-artifacts.sh` with `LLVMNET_PORTABLE_UNIVERSAL` and `LLVMNET_AOT_TOOLS`, unpacked on each host with its own `tar` (Windows: `tar.exe` on the zip), and `tests/portable-toolchain/` run against them (59 fixtures plus the launcher, archive, Fortran-module and, on Windows, `.cmd` checks):

| Host | Package | Result |
| --- | --- | --- |
| Linux x64 | `linux-x64` | 62 of 62 |
| macOS ARM64 | `osx-arm64` | 62 of 62 |
| Windows x64 | `win-x64` | 63 of 63 |

On macOS the launcher check needs `dotnet` on `PATH` (a JIT launcher runs `dotnet` from there); with only `DOTNET_ROOT` set it fails as it would for the universal package.

Compile and link of a hello program, best of three (`artifacts/xp11/time-hello.sh`), against the ReadyToRun packages of v0.3.0-dev.3 on the same Linux host and of XP-04's evidence on the others:

| Host | C | C++ (`<iostream>`) | Fortran |
| --- | ---: | ---: | ---: |
| Linux x64, ReadyToRun tools (v0.3.0-dev.3) | 1.96 s | 4.47 s | 4.32 s |
| Linux x64, NativeAOT tools | 1.12 s | 2.44 s | 2.44 s |
| macOS ARM64, ReadyToRun (XP-04) | 1.0 s | | |
| macOS ARM64, NativeAOT tools | 0.67 s | 1.53 s | 1.58 s |
| Windows x64, ReadyToRun (XP-04, PowerShell) | 2.3 s | 4.8 s | 4.2 s |
| Windows x64, NativeAOT tools (Git Bash) | 1.24 s | 2.92 s | 2.48 s |

What remains is the compiler: .NET start-up, the translation and the in-process LLVM.

## Not done

- The release workflow has not run on GitHub: the `aot-tools` jobs on `ubuntu-24.04-arm`, `windows-11-arm` and `macos-15` runners (7 GB of memory, against a 6.3 GiB peak on 12 cores here), the bootstrap preparation and the cache in the runner. `linux-arm64` and `win-arm64` packages were not built or run here.
- The compiler and the sysroots are still built with Debian's LLVM 22 in the portable job, as in the `artifacts` job; only LLVM's own build is free of it.
- The reproduction is in `artifacts/xp11/` (logs, the run scripts, the kit results).
