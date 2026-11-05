# Deployment

What a compiled program consists of, what the host that runs it needs, how the toolchain itself is installed, and how a release is verified.

## Output forms and what to ship

| Output | What to ship | Needs on the host |
| --- | --- | --- |
| `-o program.dll` | `program.dll`, `program.runtimeconfig.json`, `LlvmNet.Runtime.dll` (and in system mode `libprogram.llvmnet-host.so`), all in one directory | the .NET 10 runtime (`dotnet program.dll`) |
| `-o program` (a launcher) | `program` and the `program.llvmnet/` directory next to it | the .NET 10 runtime, `/bin/sh`; `LLVMNET_DOTNET` names another `dotnet` |
| `-shared -o Library.dll` | `Library.dll` with its `LlvmNet.Runtime.dll`, alongside the program or .NET application that references it | the .NET 10 runtime |
| `--nativeaot -o program` | `program` alone (system mode: plus the `--system-library` libraries, which stay dynamic dependencies) | nothing from .NET; the C library of the target |

A program linked with a [native extension](extensions.md) also ships the extension's assembly (`LlvmNet.Ext.Gles.dll`), which the link copies beside the image and NativeAOT compiles into the executable. The host library it stands for is not part of any output form: it is the host's (Mesa's `libEGL.so.1` on Linux) or the application's, shipped beside the image or the executable (ANGLE's `libEGL.dll` and `libGLESv2.dll` on Windows, the `.dylib` pair on macOS) or found through `LLVMNET_EXT_<NAME>_DIR`; without it the program asks `llvmnet_ext_available` or ends with one `llvmnet:` line when it calls the library.

An image's `runtimeconfig.json` pins the .NET 10 band (`rollForward: Minor`): patch and minor updates of .NET 10 are accepted, a host with only a later major refuses to run it until `DOTNET_ROLL_FORWARD=LatestMajor` is set. It also turns off the JIT's dynamic profile-guided optimization (`System.Runtime.TieredPGO: false`), which made translated programs about 7% slower on average; `DOTNET_TieredPGO=1` turns it back on for a run. And it starts the JIT's call counting at a method's first call (`System.Runtime.TieredCompilation.CallCountingDelayMs: 0`): by default the runtime waits until 100 ms pass in which no method is called for the first time before it promotes hot methods to optimized code, and translated programs keep calling functions for the first time, so their call-heavy code stayed unoptimized far longer. Without the wait the benchmark set takes 12% less time under the JIT, ffmpeg's decodes 7 to 22%, and the slowest-affected short programs lose a few milliseconds; `DOTNET_TC_CallCountingDelayMs=100` restores the default for a run. A .NET application that references a translated library (`-shared`) runs with its own `runtimeconfig.json`; it gets the same settings from `<TieredPGO>false</TieredPGO>` and `<ItemGroup><RuntimeHostConfigurationOption Include="System.Runtime.TieredCompilation.CallCountingDelayMs" Value="0" /></ItemGroup>` in its project file. A host the runtime cannot support (32-bit, big-endian, Mono, .NET before 10) prints one `llvmnet:` line naming the problem and the fix and exits with status 1 before any translated code runs.

## Supported hosts

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


## NativeAOT

`--nativeaot` publishes the image as a native executable with the .NET 10 SDK's NativeAOT toolchain: the runtime is compiled in, startup is immediate, and no `dotnet` is needed to run it. The options are `--runtime-id <rid>` (default: this host), `--aot-optimize Balanced|Speed|Size`, `--aot-instruction-set baseline|x86-64-v2|x86-64-v3|native` (on x64 the default is `x86-64-v2`, SSE4.2 and POPCNT, for a program with vector code, which includes most programs built at `-O2`, and for one with SIMD128 or `x86-64-v2` objects; `x86-64-v3` for one with `x86-64-v3` objects; otherwise the baseline, which starts on every x64 processor and which `baseline` asks for; `native` targets the build host's CPU and may not run on older CPUs; [simd.md](simd.md#choosing-a-level)), `--aot-debug` (keep symbols and the map file) and `--aot-project <dir>` (write the project instead of publishing). System mode needs `linux-x64`.

Before `dotnet publish` runs, the driver checks what it can see from the build host and reports a missing prerequisite in one line naming the fix (`NativeAOT prerequisite missing: ...`): the .NET 10 SDK (not just the runtime), a C compiler and linker (`clang` or `gcc` on `PATH`, Debian: `apt-get install clang zlib1g-dev`), zlib's development files, the aarch64 cross sysroot for `linux-arm64`, a package missing from an offline feed, or a target that must be published on another host. What the SDK finds out later surfaces as its own error.

**Offline hosts.** The first publish for a runtime identifier restores the ILCompiler packages, the runtime pack and the ILLink tasks from NuGet. `scripts/nativeaot-feed.sh <feed-dir> [rid...]` fills a folder feed with those packages on a host that has access (reusing the user's package cache where it already has them), and `LLVMNET_NUGET_FEED=<feed-dir>` makes every generated NativeAOT project restore from that feed alone, including the projects `--aot-project` writes for other hosts.

```sh
scripts/nativeaot-feed.sh /srv/llvmnet-feed linux-x64 linux-arm64                     # with network access
LLVMNET_NUGET_FEED=/srv/llvmnet-feed llvmnet-clang program.c --nativeaot -o program   # without
```

## The toolchain package

`scripts/package-toolchain.sh <prefix> [--self-contained] [--bundle-llvm]` installs a relocatable Linux x86-64 toolchain, which is also what the release archives contain:

- `bin/llvmnet-clang`, `bin/llvmnet-clang++`, `bin/llvmnet-flang`: the compiler entry points; `bin/llvmnet` selects the frontend from each input's extension.
- `bin/llvmnet-ar`, `bin/llvmnet-ranlib`, `bin/llvmnet-nm`: LLVM's archive and symbol tools for bitcode objects.
- `lib/llvmnet/`: the compiler, its LLVM bridge and the runtime assembly; with `--self-contained`, the compiler's own .NET runtime.
- `libexec/llvm/` (with `--bundle-llvm`): Clang, Flang, `opt`, the archive tools, `libLLVM` and the libraries they load, Clang's builtin headers and Flang's intrinsic modules. Copy a bundled LLVM as a whole: its library aliases must resolve to one library instance.
- `sysroot/dotnet64-v2/`: the portable sysroot (musl, libc++, libc++abi, the OpenMP runtime and flang-rt headers and bitcode archives, and compiler-rt's builtins); `sysroot/system-linux-x64-v1/`: flang-rt built against glibc for system mode; `sysroot/simd128/`: the SIMD128 header.
- `share/llvmnet/`: the documentation, the CMake and Meson files (`cmake/`, `meson/`), the SPEC configuration (`spec/`), the license, the third-party notices and the Debian copyright files of bundled LLVM packages.

A self-contained, bundled installation needs neither `dotnet` nor LLVM on `PATH` to compile (`tests/deployment.sh` checks this with an empty `PATH`), only a compatible Linux x86-64 host with Bash, ordinary shell utilities and the OS libraries LLVM and .NET load. Publishing NativeAOT executables additionally needs the .NET 10 SDK and the target's native toolchain, as above. `LLVMNET_ROOT` names the toolchain root when the compiler is run from elsewhere.

## Release packages

Each release on GitHub carries `llvmnet-<version>-linux-x64.tar.xz` (thin: LLVM 22 expected on `PATH`), `llvmnet-<version>-linux-x64-llvm.tar.xz` (LLVM bundled), `llvmnet.<version>.nupkg` (the dotnet tool), `SHA256SUMS`, and a `.sigstore.json` bundle for each file, signed by the release workflow through GitHub's OIDC identity ([dev/release-process.md](../dev/release-process.md)). To verify a download:

```sh
sha256sum -c SHA256SUMS
cosign verify-blob --bundle llvmnet-0.3.0-linux-x64.tar.xz.sigstore.json \
  --certificate-identity-regexp '^https://github.com/<owner>/llvmnet/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com llvmnet-0.3.0-linux-x64.tar.xz
```

The archives are reproducible from the tag: a second build of the same commit gives byte-identical files, and the workflow checks this before signing.
