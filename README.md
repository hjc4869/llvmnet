# llvmnet

Clang/Flang 22 -> LLVM IR -> unsafe .NET 10 CIL.

This is a working experimental compiler, not an emscripten-equivalent finished toolchain. C, C++, and Fortran share a direct CIL backend. Generated programs execute translated methods on .NET; they do not invoke native ffmpeg, interpret LLVM IR, or embed a WebAssembly engine.

The initial focus is feature correctness and single-process execution. The supplied software ffmpeg profile builds with dav1d, libxml2, and zlib. It has decoded all 1,150 frames of the supplied 4K HDR HEVC video with the same active-pixel hashes as native clang. Representative SPEC development workloads run after that ffmpeg gate. See [docs/status.md](docs/status.md) for precise coverage and limitations.

A relocatable clang/flang-style distribution, two explicit ABI modes, NativeAOT output, and ordinary SPEC harness configuration are now implemented. See [docs/toolchain.md](docs/toolchain.md) for contracts, deployment requirements, and current limits.

## Build

Requirements for the current Linux toolchain:

- .NET 10 SDK and runtime.
- LLVM/Clang/Flang 22, development headers, `libLLVM-22.so`, `llvm-config-22`, `opt-22`, `llvm-ar-22`, and `llvm-ranlib-22`.
- Bash and a C++ compiler (`clang++-22`) for the compile-time LLVM bridge.
- NuGet access for pinned `MathNet.Numerics` 5.0.0, used for `erf`.
- For dependency integration: CMake, Ninja, Meson, pkg-config, and initialized source checkouts.

```sh
dotnet build -c Release
bin/llvmnet tests/c/runtime.c -o artifacts/example.dll
dotnet artifacts/example.dll payload
```

Create the output directory first if it does not exist. Keep the emitted assembly, its runtime configuration, and the copied runtime/dependency assemblies together. LLVM and the compiler's native bridge are needed at compile time, not to run the result.

## Compiler Modes

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

Objects and archives contain LLVM bitcode, not native object code. Explicit object files are linked eagerly; archive members are selected to satisfy unresolved symbols, with rescanning for inter-archive dependencies. `-lc` and `-lm` select managed exports in managed modes and real system libraries in system mode.

`--keep-ir linked.bc` preserves linked IR for investigation. `LLVMNET_TRACE=1` enables compiler exception traces. `LLVMNET_CLANG` and `LLVMNET_FLANG` override frontend executables. Use `bin/llvmnet --help` for the supported driver interface; it is not yet a complete clang/emcc flag replacement.

## Managed Versus Native Linking

These are deliberately different operations:

```sh
bin/llvmnet --emit-library library.c -o Example.dll
bin/llvmnet main.c --cil-reference Example.dll -o managed-client.dll

bin/llvmnet -fno-builtin tests/c/native-import.c \
  --native-import 'abs=libc.so.6!abs' -o native-client.dll
```

Managed references resolve public static methods annotated with `LlvmNet.Runtime.CExportAttribute`, also emitted on translated methods. Scalar signatures and native-width pointer values must match. Cross-assembly aggregate signatures are not yet a stable public ABI.

The explicit `--native-import` path creates opt-in P/Invoke declarations, one symbol mapping per argument. Only non-variadic scalar C signatures are supported by this path; aggregate/by-value/sret signatures are rejected. This path does not supply native callbacks or native data imports. There is no implicit native-library fallback in the managed modes.

That restriction applies to the explicit scalar `--native-import` path. The newer `--runtime=system` mode uses LLVM-generated native ABI bridges and supports tested C struct/varargs calls, native data imports, and scalar callbacks. Native C++ library ABI interchange remains unsupported.

## Deploy and Select an ABI

```sh
bash scripts/package-toolchain.sh /path/toolchain --self-contained --bundle-llvm
/path/toolchain/bin/llvmnet-clang --runtime=system source.c --nativeaot -o program
/path/toolchain/bin/llvmnet-clang --runtime=portable source.c -o portable.dll
/path/toolchain/bin/llvmnet-clang++ --runtime=portable source.cpp -o cpp.dll
/path/toolchain/bin/llvmnet-flang --runtime=portable source.f90 -o fortran.dll
```

`system` uses the native Linux x86-64 ABI and libc/pthread interfaces. `portable` uses the bundled, versioned `dotnet64-v1` ABI and managed adapters, with libc++ headers/support bitcode included. The existing `managed-host` default remains a legacy compatibility mode, not a cross-platform ABI. Objects and managed references from different modes are rejected.

Outputs without `.dll` are ordinary Unix executable launchers plus a payload directory; `--nativeaot` produces a native executable with .NET runtime code included. NativeAOT publishing needs the .NET 10 SDK and native linker even when the compiler installation is self-contained. Cross-platform execution of portable CIL is a release goal with a fixed ABI, but has only been tested on Linux x86-64 so far.

## Tests and Workloads

```sh
bash tests/all.sh core
bash tests/all.sh integration
bash tests/toolchain-all.sh

bash scripts/build-ffmpeg.sh
bash tests/codecs.sh
bash tests/codecs-extended.sh
bash tests/manifests.sh
bash scripts/benchmark-video.sh

bash scripts/spec-mcf.sh test
bash scripts/spec-mcf.sh refrate
bash scripts/spec-deepsjeng.sh test
bash scripts/spec-exchange2.sh test
bash scripts/spec-zstd.sh
bash scripts/spec-lbm.sh 2017 test
bash scripts/spec-lbm.sh 2026 test
```

Run `scripts/build-dependencies.sh` before the full ffmpeg build. Its defaults use the supplied LightPlayer-Packages checkout and cached zlib 1.3.2. Override `LIGHTPLAYER_PACKAGES`, `FFMPEG_SOURCE`, `LLVMNET_DEPS_PREFIX`, or `ZLIB_SOURCE` when needed. The smaller [scripts/build-ffmpeg-core.sh](scripts/build-ffmpeg-core.sh) is useful for backend bring-up.

Build outputs, generated media, and locally extracted SPEC materials stay under ignored `artifacts/`. The scripts do not modify the original source checkouts or mounted SPEC kits. SPEC development scripts stage only the relevant licensed benchmark archives. A standard harness runner and configuration are also available via [scripts/spec-harness.sh](scripts/spec-harness.sh). These checks are not full-suite SPEC-reportable runs or scores.

## Safety and Portability

Generated code is intentionally unsafe and is not a sandbox. C pointers, invalid memory accesses, or untrusted programs can corrupt memory or terminate the process. Only compile and execute code you trust.

Host-ABI modes target Linux/x86-64 SysV. Portable mode uses a fixed little-endian 64-bit .NET ABI with owned headers and runtime structures, not host headers. None of the modes is a sandbox or a complete libc/POSIX implementation. See [docs/toolchain.md](docs/toolchain.md), [docs/architecture.md](docs/architecture.md), and [docs/roadmap.md](docs/roadmap.md).