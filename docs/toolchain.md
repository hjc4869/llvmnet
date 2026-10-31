# Toolchain and ABI Modes

## Installation

`scripts/package-toolchain.sh` installs a relocatable Linux x86-64 toolchain:

- `bin/llvmnet-clang`, `bin/llvmnet-clang++`, `bin/llvmnet-flang`: compiler entry points.
- `bin/llvmnet`: selects the frontend from each input's extension.
- `bin/llvmnet-ar`, `bin/llvmnet-ranlib`, `bin/llvmnet-nm`: LLVM's archive and symbol tools for bitcode objects.
- `lib/llvmnet/`: the compiler, its LLVM bridge and the runtime assembly.
- `libexec/llvm/`: with `--bundle-llvm`, Clang, Flang, `opt`, the archive tools, `libLLVM` and the libraries they load, Clang's builtin headers (only the `include/` part of the resource directory: its sanitizer and profiling runtimes cannot be linked by llvmnet) and Flang's intrinsic modules, stripped of what a run does not need (TC-13).
- `sysroot/dotnet64-v2/`: the portable sysroot (musl, libc++, libc++abi, the OpenMP runtime and flang-rt headers and bitcode archives, and compiler-rt's complex arithmetic builtins); `sysroot/system-linux-x64-v1/`: flang-rt built against glibc for system mode; `sysroot/simd128/`: the SIMD128 header. `scripts/build-sysroot.sh` builds the first two.
- `share/llvmnet/`: documentation, the SPEC configuration (`share/llvmnet/spec/`), license and third-party notices, and the Debian copyright files of bundled LLVM packages.

```sh
bash scripts/package-toolchain.sh /path/to/prefix
bash scripts/package-toolchain.sh /path/to/prefix --self-contained --bundle-llvm
export PATH=/path/to/prefix/bin:$PATH
```

`--self-contained` bundles the compiler's .NET runtime and `--bundle-llvm` the LLVM tools, so the installation needs neither `dotnet` nor LLVM on `PATH`; `tests/deployment.sh` checks this. It still needs a compatible Linux x86-64 host, Bash, ordinary shell utilities and the OS libraries LLVM and .NET load. Copy a bundled LLVM as a whole: its library aliases must resolve to one library instance.

Publishing NativeAOT executables additionally needs the .NET 10 SDK with its NativeAOT packages and the target's native linker and development libraries, which the package does not include. A NativeAOT executable does not need `dotnet` to run.

`cmake/llvmnet.cmake` and `meson/llvmnet.ini` configure CMake and Meson projects to build with `bin/llvmnet` and LLVM's archiver; `scripts/build-dependencies.sh` uses them.

## Driver Contract

[user/driver.md](user/driver.md) is the driver reference: the entry points, the steps and output forms, the defaults, and the generated tables of every option llvmnet handles, accepts without effect or rejects, with the reason for each. Every other option is passed to Clang or Flang. In short: the driver implements the compile, link and output steps (`-c`, `-E`, `-S -emit-llvm`, `-fsyntax-only`, the `-M` family, `-o`, `-r`, `-shared`, `-x`, `-###`, response files), link inputs (`-L`, `-l`, `-l:`, `-nostdlib` and its variants, `-Wl,--whole-archive` and the group markers), the runtime modes and NativeAOT options; it drops native-only options such as `-fPIC` and `-Wl,-rpath`; and it rejects, with a reason, native linker options without a CIL equivalent and, in portable mode, every option that would change the versioned ABI (`-m<option>`, `--target=`, `-fshort-wchar`, ...). `tests/driver-options.sh` feeds every option Clang's and Flang's `--help` list to the driver in both modes, and each one works or fails with a clear error.

The aliases select their language even when a file name is ambiguous: `llvmnet-clang++` uses Clang's C++ driver mode and `llvmnet-flang` uses Flang; `--driver-mode=` does the same without the aliases. Inputs ending in `.o` or `.a` must contain LLVM bitcode.

The default is scalar `-O1` with automatic vectorization disabled. `-O3 -fvectorize -fslp-vectorize` produces vector IR, which LLVM's reduction expansion and scalarizer turn into scalar CIL; remaining fixed-vector values keep LLVM's packed layout. `--keep-ir` keeps the linked bitcode before this lowering.

`--simd128` enables a wasm-compatible SIMD128 source header backed by .NET `Vector128<T>` helpers. It keeps vector memory operations and helper values within the existing data ABI of either mode. The ffmpeg profile uses it for HEVC IDCT and 8-bit SAO with `FFMPEG_SIMD128=1`. It does not accelerate auto-vectorized LLVM IR; see [simd128.md](simd128.md).

The output form is independent of the runtime mode:

| Choice | Result |
| --- | --- |
| `-o program.dll` | CIL assembly, `.runtimeconfig.json` and `LlvmNet.Runtime.dll` |
| `-o program` or no `-o` | Unix launcher script plus `program.llvmnet/` payload; default name `a.out` |
| `-shared` / `--emit-library` | Managed CIL library (default `a.dll`), not a native shared object |
| `--nativeaot -o program` | NativeAOT executable for one runtime identifier |
| `--emit-llvm -o program.bc` | Linked LLVM bitcode |
| `-r -o combined.o` | Relocatable linked bitcode |

NativeAOT options are `--runtime-id <rid>`, `--aot-optimize Balanced|Speed|Size`, `--aot-instruction-set baseline|native`, `--aot-debug` and `--aot-project <dir>` (write the project for publishing on another host, see [NativeAOT on other hosts](#nativeaot-on-other-hosts)); they require `--nativeaot`, which in turn requires an executable link. The instruction set defaults to `baseline`; `native` targets the build host's CPU, requires the host's runtime identifier, and produces binaries that may not run on older CPUs. Baseline NativeAOT code with `--simd128` can be slower than scalar code (PF-08; see [video-simd128-performance.md](video-simd128-performance.md)). System mode requires `linux-x64`. Other runtime identifiers need a matching native toolchain: `linux-arm64` can be cross-published from Linux x64, and `win-x64` and `osx-arm64` are published on those hosts.

## Supported Hosts

| | Linux x64 | Linux ARM64 | Windows x64 | macOS ARM64 |
| --- | --- | --- | --- | --- |
| Portable images (`dotnet64-v2`), JIT | yes | yes | yes | yes |
| Portable images, NativeAOT | yes | yes, published on the host or [cross-published](#nativeaot-on-other-hosts) from Linux x64 | yes | yes |
| System ABI (`system-linux-x64-v1`) | yes | no | no | no |
| The compiler | yes | no | no | no |

.NET 10 (LTS) is required; an image's `runtimeconfig.json` pins the framework band (`rollForward: Minor`), so a host with only a later major version refuses to run it until `DOTNET_ROLL_FORWARD=LatestMajor` is set. A host the runtime cannot support (32-bit, big-endian, Mono, .NET before 10) prints one `llvmnet:` line naming the problem and the fix and exits with status 1 before any translated code runs. The `tests/xp/` bundle (`tests/xp.sh`) checks the matrix: the fixtures in `tests/xp/fixtures.tsv` are built natively and in portable mode on Linux x64, and the portable images must reproduce the native output and exit status on each host under the JIT and as NativeAOT executables, which the CI jobs on Windows, macOS and Linux ARM64 runners do.

### NativeAOT on other hosts

The compiler runs on Linux x64, so a NativeAOT executable for another host is published there from a relocatable project: `--aot-project <dir>` writes `NativeHost.csproj`, the image and `LlvmNet.Runtime.dll` into `<dir>` instead of publishing, and on the target host `dotnet publish <dir>/NativeHost.csproj -c Release -r <rid> -o <out>` produces `<out>/program` (`program.exe` on Windows). The NativeAOT options (`--aot-optimize`, `--aot-debug`) are recorded in the project; the runtime identifier is chosen at publish time.

```sh
llvmnet-clang --runtime=portable program.c --aot-project program-aot   # on Linux
dotnet publish program-aot/NativeHost.csproj -c Release -r win-x64 -o program-win   # on Windows
```

Each host needs the .NET 10 SDK, the `Microsoft.DotNet.ILCompiler` package for the runtime identifier (restored from NuGet at the first publish) and its native toolchain:

| Target | Native toolchain |
| --- | --- |
| `linux-x64`, `linux-arm64` on the host itself | `clang` and `zlib1g-dev` (Debian/Ubuntu package names) |
| `linux-arm64` from a Linux x64 host | `clang`, `libc6-dev-arm64-cross`, `libgcc-<version>-dev-arm64-cross` and `binutils-aarch64-linux-gnu`; then `--nativeaot --runtime-id linux-arm64` publishes directly. The generated project selects `aarch64-linux-gnu-objcopy` for the strip step. |
| `win-x64` | Visual Studio 2022 or later with the "Desktop development with C++" workload (the MSVC linker and Windows SDK) |
| `osx-arm64` | Xcode or the Xcode command-line tools |

Before `dotnet publish` runs, the driver checks the prerequisites it can see from the build host and reports a missing one in a single line naming the fix: `NativeAOT prerequisite missing: the .NET 10 SDK ...`, `... a C compiler and linker for linux-x64. Install clang ...`, `... zlib's development files are missing ...`, `... the aarch64 cross sysroot is missing ...`, `... the offline feed <dir> has no ... package ...`, or `... win-x64 executables cannot be published from linux-x64 ...` (`tests/aot-offline.sh` checks each). What the SDK finds out later (a package missing from NuGet, a linker failure) surfaces as its own error. A NativeAOT executable needs no `dotnet` on the host that runs it.

**Offline hosts.** The first publish for a runtime identifier restores the ILCompiler packages, the runtime pack and the ILLink tasks from NuGet. `scripts/nativeaot-feed.sh <feed-dir> [rid...]` fills a folder feed with those packages on a host that has access (reusing the user's package cache where it already has them), and `LLVMNET_NUGET_FEED=<feed-dir>` makes every generated NativeAOT project restore from that feed alone (its `nuget.config` clears the other sources), including the projects `--aot-project` writes for other hosts.

```sh
scripts/nativeaot-feed.sh /srv/llvmnet-feed linux-x64 linux-arm64      # with network access
LLVMNET_NUGET_FEED=/srv/llvmnet-feed llvmnet-clang program.c --nativeaot -o program   # without
```

## Runtime Modes

Use the same mode for every source, object, archive and managed reference of one program. LLVM module flags and managed assembly metadata carry the ABI tag, and links with mismatched or untagged inputs fail. At run time, images of different modes cannot share one process, and an image refuses a `LlvmNet.Runtime.dll` from an incompatible llvmnet version. `llvmnet --runtime=<mode> -x ir -c input.ll` compiles LLVM IR for a mode and records its tag.

### Portable .NET ABI

```sh
llvmnet-clang --runtime=portable source.c -o portable.dll
llvmnet-clang++ --runtime=portable -std=c++17 source.cpp -o portable-cpp.dll
llvmnet-flang --runtime=portable source.f90 -o portable-fortran.dll
dotnet portable.dll
llvmnet-clang --runtime=portable --nativeaot source.c -o native-program
```

Tag: `dotnet64-v2`, the default mode. Sources compile with `-nostdinc` against the sysroot's headers: musl 1.2.5 for C and libc++ 22.1.8 for C++. Executables link musl, libc++, libc++abi, LLVM's OpenMP runtime (libomp) and flang-rt 22.1.8 and the compiler-rt builtins from the sysroot as bitcode, compiled into the program's CIL; `-fopenmp` needs no further option, and the default team size is the processor count .NET reports (`DOTNET_PROCESSOR_COUNT` overrides it) unless `OMP_NUM_THREADS` is set. `-nostdlib`, `-nodefaultlibs` and `-nostartfiles` leave them out; `-lc`, `-lm`, `-lpthread`, `-ldl`, `-lrt`, `-lc++` and the Fortran runtime names are accepted and need no archive. `--portable-sysroot <dir>` selects another sysroot.

The LLVM target is a fixed `x86_64-unknown-none` layout that defines the data ABI; it does not generate x86 instructions. Host OS and CPU macros (`__linux__`, `__unix__`, `__x86_64__`, `__SSE__` and similar) are not defined; programs see `__LLVMNET__`, `__LLVMNET_PORTABLE__` and `__LLVMNET_ABI_VERSION`. Options that would change the ABI (`-target`, `--sysroot`, `-m*`, `-fshort-wchar`, `-fshort-enums`, `-fpack-struct`) are rejected. `-funsigned-char` is accepted: it changes no layout, so objects built with either signedness link together and with the signed-`char` sysroot, as they do natively, and each side reads a `char` argument or result with its own signedness.

| Type or interface | `dotnet64-v2` |
| --- | --- |
| Byte order / address space | little-endian / 64-bit |
| `char`, `short`, `int`, `long`, `long long` | 8, 16, 32, 64, 64 bits; plain `char` signed unless `-funsigned-char` |
| `size_t`, `ptrdiff_t`, pointers | 64 bits |
| `int_fast16_t`, `int_fast32_t` and unsigned forms | 64 bits, as in glibc |
| `wchar_t` | signed 32-bit |
| `float`, `double` | IEEE binary32 / binary64 |
| `long double` | 80-bit extended precision in 16-byte storage, computed in software on every host |
| `__int128` | 128-bit integer storage and helpers |
| Varargs | software SysV-style register and overflow areas, independent of the host's calling convention |
| C library structures | musl 1.2.5 x86-64 layouts (`FILE`, `jmp_buf`, `struct stat`, pthread types, `sigset_t`) |
| C++ objects | Itanium layout and libc++ ABI version 1 (`std::__1`) |
| Fortran descriptors | Flang 22 CFI version 20240719 with byte strides |
| errno and error text | Linux numbering; musl's messages |
| System services | Linux x86-64 system-call semantics implemented over .NET ([dev/host-abi.md](dev/host-abi.md)) |

The same image runs on every supported host ([Supported hosts](#supported-hosts)). Programs see Linux semantics and structure layouts on every host. Paths, file-system capabilities, time-zone data and available resources still depend on the host, and services .NET cannot provide (`fork`, `exec`, sockets, hard links and others) fail with the errors [dev/host-abi.md](dev/host-abi.md) lists; [its last section](dev/host-abi.md#hosts-other-than-linux) describes what Windows and macOS do differently.

Native libraries cannot be linked: `--system-library` is rejected, and a function the sysroot does not provide is a link error. Recompile dependencies in portable mode or call them through a managed interface. Portable libraries (`-shared`) do not link the C library, because musl's process state (thread pointer, heap, stdio) belongs to the program image; a library that calls the C library must be linked into the program as bitcode. Use scalar and pointer `CExport` signatures across assemblies; aggregate signatures are not yet a stable API.

### System ABI

```sh
llvmnet-clang --runtime=system -c source.c -o source.o
llvmnet-clang --runtime=system source.o -lm -pthread --nativeaot -o program
llvmnet-clang --runtime=system source.c --system-library /path/libexample.so -o program.dll
```

Tag: `system-linux-x64-v1`. This mode uses the native Linux x86-64 SysV ABI and the host's headers and libraries. C stdio, allocation, errno, math, file interfaces, native data and most pthread calls go to the real system libraries.

LLVM generates a native bridge for each imported call signature: CIL passes a byte argument record, and LLVM emits the native call, including varargs, register-classified structures, `byval` and `sret`. Native variadic functions called through pointers, such as `fcntl`, `open` and `snprintf`, use call-site-specific LLVM thunks. C callbacks passed to native code use unmanaged-callable CIL entry points; native calls into translated variadic or aggregate callbacks fail with a diagnostic. Native globals resolve through the native library that defines them.

JIT output includes the generated bridge as `lib<name>.llvmnet-host.so`. NativeAOT links the bridge statically, so the result is one executable; libraries given with `--system-library` stay dynamic dependencies.

Threads are native pthreads; an adapter wraps thread start and TLS destructors so callbacks run inside .NET's thread lifetime, including under NativeAOT. Cancellation, `pthread_exit`, recovery from hardware-fault signals, external native TLS globals and TLS destructors on threads created by native libraries are incomplete. C++ exceptions use the managed bridge, and C++ library code must be linked as bitcode (for example the optional libstdc++ archive below); native libstdc++ is not called. Fortran programs link flang-rt from `sysroot/system-linux-x64-v1`.

Inline assembly is rejected except for empty or comment-only `asm` statements without outputs, which become a memory barrier in both modes. System mode also accepts the x86 `int3` trap, which raises `SIGTRAP`, and the CPUID and XGETBV forms used by LLVM's and Eigen's host detection, which run in generated native thunks.

By default, a void call to a translated function with fewer arguments than its definition is a compile error. `--trap-missing-arguments` (both modes) instead compiles the call site to throw `InvalidProgramException` and reports each such call while compiling.

### Optional System C++ Library

[../scripts/build-system-cxx.sh](../scripts/build-system-cxx.sh) compiles selected, unmodified libstdc++ 16 sources to system-mode bitcode against the matching host headers. The archive provides streams, locales, containers, threads, futures, `random_device`, regular expressions, demangling and `std::filesystem`. Obtain the libstdc++ source of the host's GCC 16 release separately, for example the `libstdc++-v3`, `libiberty` and `include` directories of Debian's `gcc-16-source` package.

```sh
bash scripts/build-system-cxx.sh artifacts/vendor/gcc-16.2.0/libstdc++-v3
tests/run.sh system-cxx
bin/llvmnet --runtime=system program.cpp artifacts/system-cxx/libstdcxx-system.a -o program
```

`tests/system-cxx.sh` compares C++ fixtures with native output at `-O0` and `-O3` under JIT and NativeAOT. Keep the source notices and the GPL with the GCC Runtime Library Exception with any redistributed archive; the package script does not build or include it.

### Optional System OpenMP

System-mode OpenMP links LLVM's native C runtime; outlined regions stay translated CIL and enter through generated callback thunks. Stage the Debian headers and runtime without root privileges and run the tests:

```sh
bash scripts/install-system-openmp.sh
tests/run.sh openmp fortran-concurrent
```

The stage defaults to `artifacts/system-openmp` with package version `1:22.1.8-1+b2` (`OPENMP_DEB_VERSION`) and keeps the package hashes and the copyright file. `LLVMNET_OPENMP_PREFIX` selects another stage for the tests. Programs need the library on the loader path (`LD_LIBRARY_PATH` or a system installation); it is not bundled. Flang's `-fdo-concurrent-to-openmp=host`, which Flang 22 marks experimental, uses the same runtime. Portable mode has no OpenMP runtime, and device offload is not supported.

## Managed and Native Libraries

`-L` and `-l` always name bitcode archives. `--cil-reference` names an assembly with public `CExport` methods and matching ABI metadata. `--system-library` adds a native shared library for system-mode symbol resolution; every native function, scalar or not, goes through the generated bridges. The earlier `--native-import symbol=library!entrypoint` option (an explicit scalar P/Invoke declaration) was removed in favor of this one path (ADR-04, SA-01) and now fails with that guidance.

## Standard SPEC Configuration

[../config/spec/llvmnet.cfg](../config/spec/llvmnet.cfg) sets the ordinary `CC`, `CXX` and `FC` variables to the installed compiler aliases. The runtime mode is passed at compile and link time and `--nativeaot` at link time, so the harness stages an ordinary native executable.

SPEC builds default to `-O3`, C++14 for CPU 2017 and C++17 for CPU 2026. Build labels include the runtime, optimization, vectorization, C++ dialect and thread count. Environment overrides:

| Variable | Effect |
| --- | --- |
| `SPEC_OPT_LEVEL`, `SPEC_VECTORIZE=1`, `SPEC_CXX_STANDARD` | optimization level, vectorization, C++ dialect |
| `SPEC_REBUILD=1` | force recompilation, for example after a toolchain update |
| `SPEC_THREADS` | thread count; rate entries default to 1, speed entries to 4. Above 1, the kit's OpenMP set gets `-fopenmp -DSPEC_OPENMP` and CPU 2026's DO CONCURRENT set host lowering |
| `SPEC_OPENMP_PREFIX`, `SPEC_OPENMP_LIBRARY` | system mode with more than one thread: staged OpenMP headers and runtime (default `artifacts/system-openmp`), linked with `--system-library` and added to `LD_LIBRARY_PATH` |
| `SPEC_SYSTEM_CXX=/abs/path/libstdcxx-system.a` | add the optional libstdc++ archive to system-mode C++ links |
| `SPEC_STACK_KB` | soft stack limit of the run (default `unlimited`; large Fortran automatic arrays exceed 8 MiB) |
| `SPEC_REUSE_BUILD=1` | the kit's `--make_no_clobber`, for diagnostic retries with unchanged flags and object ABI; matrix runs never reuse builds |

```sh
bash scripts/spec-harness.sh /path/installed-cpu2017 /path/toolchain system 505.mcf_r test
bash scripts/spec-harness.sh /path/installed-cpu2017 /path/toolchain portable 505.mcf_r test
```

The runner installs the configuration and flags description in the harness. The equivalent command, after sourcing the suite's environment, is:

```sh
runcpu --config=llvmnet --define llvmnet_dir=/path/toolchain \
  --define llvmnet_runtime=system --size=test --copies=1 --iterations=1 \
  --noreportable --tune=base 505.mcf_r
```

The configuration sets `reportable=0`. A reportable result needs full suites, SPEC's run rules and complete flag and system disclosures.

### Matrix, summary and ordered runs

```sh
bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain test
bash scripts/spec-matrix.sh artifacts/spec2026-harness artifacts/spec-toolchain test
SPEC_VECTORIZE=1 bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain test '505.*'
```

`scripts/spec-matrix.sh` runs every installed entry one at a time, system then portable, always rebuilding. It continues after failures and exits nonzero unless every case passes. A case passes only when the harness exits with 0 and prints the benchmark's `Success:` validation marker. Each run gets a directory under `artifacts/spec-matrix/` (`SPEC_MATRIX_OUTPUT`) with a TSV row per benchmark and mode, harness and make logs, the configuration and compiler and runtime hashes, and a `completion.txt` written once every row exists. `SPEC_TIMEOUT` bounds each build and run (default 1800 seconds). The optional fourth argument is a shell pattern of benchmark names. The script runs a frozen copy of itself, the harness wrapper and the configuration, so later workspace edits do not affect a running matrix; keep the installed toolchain unchanged until it finishes, and do not run two matrices against the same installed kit.

`bash scripts/spec-summary.sh first/results.tsv retry/results.tsv` prints one row per suite, benchmark, input, optimization, vectorization, copies, threads and threading profile, with separate system and portable results; later files override earlier ones and missing modes show as MISSING.

[../scripts/spec-ordered.sh](../scripts/spec-ordered.sh) runs the matrix one benchmark at a time in this group order: 2017 integer rate, 2026 integer rate, 2026 FP rate, 2026 integer speed, 2026 FP speed, 2017 FP rate, 2017 integer speed, 2017 FP speed. It stops at the first benchmark whose required modes fail (`SPEC_REQUIRED_ABIS`, `system` by default or `both`) and records COMPLETE or BLOCKED under `artifacts/spec-ordered/`. `SPEC_ORDER_START=2026-intrate:721.gcc_r` resumes at an entry.

```sh
SPEC_REQUIRED_ABIS=system SPEC_TIMEOUT=600 \
SPEC_SYSTEM_CXX="$PWD/artifacts/system-cxx/libstdcxx-system.a" \
  bash scripts/spec-ordered.sh artifacts/spec2017-harness artifacts/spec2026-harness artifacts/spec-ordered-toolchain test
```

`scripts/spec-portable-check.sh` reruns portable-mode entries against a baseline result file; [spec-portable-limitations.md](spec-portable-limitations.md) describes it and the causes of the remaining portable failures. Choose `test`, `train` or `ref` inputs explicitly. Licensed sources, inputs and run outputs stay under the ignored `artifacts/` directory.

## Verification

`tests/run.sh --tag core --tag toolchain` covers compiler regressions, both modes, native C structures, varargs, data and callbacks, NativeAOT, portable C, C++ and Fortran, managed links and mode-mismatch rejection, packaging, and deployment without `dotnet`, `clang` or `flang` on `PATH`. The `integration`, `optional` and `conformance` tags cover ffmpeg, OpenMP, the system libstdc++ archive and the upstream library test suites.
