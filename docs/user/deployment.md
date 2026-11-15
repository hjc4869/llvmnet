# Deployment

What a compiled program consists of, what the host that runs it needs, how the toolchain itself is installed, and how a release is verified.

## Output forms and what to ship

| Output | What to ship | Needs on the host |
| --- | --- | --- |
| `-o program.dll` | `program.dll`, `program.runtimeconfig.json`, `LlvmNet.Runtime.dll` (and in system mode `libprogram.llvmnet-host.so`), all in one directory | the .NET 10 runtime (`dotnet program.dll`) |
| `-o program` (a [launcher](#launchers)) | `program` and the `program.llvmnet/` directory next to it; symbolic links to `program` may be anywhere | the .NET 10 runtime, `/bin/sh` (and `readlink` when started through a symbolic link); `LLVMNET_DOTNET` names another `dotnet` |
| `--readytorun <rid> -o program.dll` (or a launcher name) | the same files and `program.r2r.dll`, the precompiled code of the image and `LlvmNet.Runtime.dll` | the .NET 10 runtime on a host of that runtime identifier |
| `-shared -o Library.dll` | `Library.dll` with its `LlvmNet.Runtime.dll`, alongside the program or .NET application that references it | the .NET 10 runtime |
| `--nativeaot -o program` | `program` alone (system mode: plus the `--system-library` libraries, which stay dynamic dependencies) | nothing from .NET; the C library of the target |

A program linked with a [native extension](extensions.md) also ships the extension's assembly (`LlvmNet.Ext.Gles.dll`), which the link copies beside the image and NativeAOT compiles into the executable. The host library it stands for is not part of any output form: it is the host's (Mesa's `libEGL.so.1` on Linux) or the application's, shipped beside the image or the executable (ANGLE's `libEGL.dll` and `libGLESv2.dll` on Windows, the `.dylib` pair on macOS) or found through `LLVMNET_EXT_<NAME>_DIR`; without it the program asks `llvmnet_ext_available` or ends with one `llvmnet:` line when it calls the library.

An image's `runtimeconfig.json` pins the .NET 10 band (`rollForward: Minor`): patch and minor updates of .NET 10 are accepted, a host with only a later major refuses to run it until `DOTNET_ROLL_FORWARD=LatestMajor` is set. It also turns off the JIT's dynamic profile-guided optimization (`System.Runtime.TieredPGO: false`), which made translated programs about 7% slower on average; `DOTNET_TieredPGO=1` turns it back on for a run. And it starts the JIT's call counting at a method's first call (`System.Runtime.TieredCompilation.CallCountingDelayMs: 0`): by default the runtime waits until 100 ms pass in which no method is called for the first time before it promotes hot methods to optimized code, and translated programs keep calling functions for the first time, so their call-heavy code stayed unoptimized far longer. Without the wait the benchmark set takes 12% less time under the JIT, ffmpeg's decodes 7 to 22%, and the slowest-affected short programs lose a few milliseconds; `DOTNET_TC_CallCountingDelayMs=100` restores the default for a run. A system-mode program that calls `fork`, `vfork`, `_Fork`, `forkpty` or `daemon` keeps the default, because a child forked while the runtime compiles for tier 1 on its background thread inherits that thread's locks and can hang. A .NET application that references a translated library (`-shared`) runs with its own `runtimeconfig.json`; it gets the same settings from `<TieredPGO>false</TieredPGO>` and `<ItemGroup><RuntimeHostConfigurationOption Include="System.Runtime.TieredCompilation.CallCountingDelayMs" Value="0" /></ItemGroup>` in its project file. A host the runtime cannot support (32-bit, big-endian, Mono, .NET before 10) prints one `llvmnet:` line naming the problem and the fix and exits with status 1 before any translated code runs.

### Launchers

A launcher is a POSIX shell script that runs `dotnet program.llvmnet/program.dll`. It finds its payload beside its own file, following symbolic links to it as the kernel does, so it can be linked from another directory (`/usr/local/bin/program` to `/opt/program/bin/program`) or under other names. The program sees what a native program started the same way sees:

- `argv[0]` is what the launcher was started as (`./program`, `../bin/alias`, `/opt/program/bin/program`), so usage texts and programs that choose their behavior by their name (`llvm-ar` started as `ranlib`, `clang` as `clang++`) work.
- `/proc/self/exe` names the launcher with every link resolved: `readlink`, `realpath`, `stat` and `open` see it there, and `posix_spawn` of it starts the launcher. Programs that find files relative to themselves (Clang's resource directory) or start themselves again (Flang's driver runs `<self> -fc1`) work.
- The launcher passes the two to the runtime in `LLVMNET_ARGV0` and `LLVMNET_EXE`, which the runtime takes out of the environment, so neither the program nor its children see them. A launcher of one's own (a packager's script, a `.cmd` file on Windows) can set them the same way.

What still differs from a native program:

- Started through `PATH` (or bash's `exec -a`), a native program sees the name it was started by, `program`; a launcher sees the path the shell found, `/usr/local/bin/program`, because the kernel gives a script that path as `$0`. The base name is the same.
- In system mode, glibc asks the kernel for `/proc/self/exe`, which names the `dotnet` host; `argv[0]` is the launcher's.
- A child started with `posix_spawn`, `system` or `popen` gets the path of its executable as `argv[0]`, not the one its parent passed ([LC-33](../plan/02-portable-c-library.md)).

`dotnet program.dll` and `.dll` outputs keep the image's path as `argv[0]` and the `dotnet` host as `/proc/self/exe`. NativeAOT executables are native programs and see their own name and path. `tests/launcher-identity.sh` compares all three forms with the native build ([evidence](../evidence/2026-11-02-tc21-launcher-identity.md)).

## Supported hosts

| | Linux x64 | Linux ARM64 | Windows x64 | macOS ARM64 |
| --- | --- | --- | --- | --- |
| Portable images (`dotnet64-v2`), JIT | yes | yes | yes | yes |
| Portable images, NativeAOT | yes | yes, published on the host or [cross-published](#nativeaot-on-other-hosts) from Linux x64 | yes | yes |
| System ABI (`system-linux-x64-v1`) | yes | no | no | no |
| The compiler, [portable toolchain](#the-portable-toolchain) | yes | yes | yes | yes |
| The compiler, native-LLVM packages | yes | no | no | no |

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

## ReadyToRun

`--readytorun <rid>` precompiles a JIT image for one runtime identifier (`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-x64` or `osx-arm64`; system mode: `linux-x64`). crossgen2 compiles the image and `LlvmNet.Runtime.dll` into one composite image, `program.r2r.dll` beside them, and the JIT starts from that optimized code instead of compiling every method at its first call. It still recompiles hot methods for tier 1, so the program keeps everything the JIT offers (tiering, loading extensions, reflection), and a function too large for the JIT to optimize ([limitations.md](limitations.md)) runs crossgen2's optimized code instead of minimally optimized code. The image then loads only on hosts of that identifier, which is why the portable image stays IL-only; it runs on every .NET 10 runtime of that identifier, patch releases included. A build takes seconds (ffmpeg's decoder about 2 s).

Use it for a deployment whose host is known when the program's time goes into start-up and compilation (tramp3d 0.35 of the JIT's time, lame 0.66, sha 0.63, the startup probe 32 ms against 40) or into functions past the JIT's limits, which `--diagnose-limits` lists (ffmpeg's H.264 decode 0.81 of the time on one thread, 0.63 on four). Over the benchmark set it takes 0.913 of the JIT's time. Keep the IL image for a program that spends its time in one long loop of a function it calls once: precompiled code is not replaced by on-stack replacement and does not know which classes are initialized, so such loops run as crossgen2 compiled them (dhrystone 1.11 of the JIT's time, fannkuch and memcpy 1.06); timing both builds of the program is the reliable test (`scripts/benchmark.py --engines native,jit,jit-r2r` for the benchmark set).

The build needs the .NET 10 SDK: a restore of a generated project brings crossgen2 for this host and the runtime pack of the target from NuGet, the user's package cache or the offline feed of [NativeAOT's offline hosts](#nativeaot) (`LLVMNET_NUGET_FEED`; `scripts/nativeaot-feed.sh` fills it with these packages too). A missing package is one line naming the feed and the fix.

## The portable toolchain

The portable toolchain runs on every .NET 10 host: the compiler is a framework-dependent .NET program, and the LLVM it calls in-process and the tools it runs (Clang, Flang, `opt`, `llvm-ar`, `llvm-nm`) are built with llvmnet itself, in portable mode ([scripts/selfhost/README.md](../../scripts/selfhost/README.md)). It needs the .NET 10 runtime and nothing else; `scripts/package-toolchain.sh <prefix> --portable` makes one from a checkout.

- `bin/`: `llvmnet`, `llvmnet-clang`, `llvmnet-clang++`, `llvmnet-flang`, `llvmnet-ar`, `llvmnet-ranlib` and `llvmnet-nm` as POSIX scripts (bash 3.2 or later, so macOS's own bash) and as `.cmd` files for Windows. They find `dotnet` through `LLVMNET_DOTNET`, `DOTNET_ROOT`, `PATH` and then the usual install locations, and when there is none, or it has no .NET 10 runtime, they print what to install and exit (127 and 150).
- `lib/llvmnet/`: `llvmnet.dll`, `LlvmNet.Runtime.dll` and `LlvmNet.Llvm.dll`, LLVM 22's C API and llvmnet's bridge as one managed library that embeds its C library ([linking.md](linking.md#libraries-that-embed-the-c-library)). The compiler calls it instead of a native `libLLVM` (`LLVMNET_LLVM=native` or a path overrides the choice).
- `libexec/llvm/`: the tools as portable images with a launcher pair per name (`clang-22`, `clang++-22`, `flang-22`, `opt-22`, `llvm-ar-22`, `llvm-ranlib-22`, `llvm-nm-22` and the names without the version), Clang's builtin headers and Flang's intrinsic modules. The compiler starts a tool's payload with `dotnet` directly, as the host layer starts any llvmnet launcher a program spawns, so Flang's driver runs `<self> -fc1` on Windows too.
- `sysroot/` and `share/llvmnet/` as in [the toolchain package](#the-toolchain-package), without symbolic links, so the same tree is the `.tar.xz` and the `.zip`.

**Precompiled packages.** Under the JIT every tool compiles itself at each start: compiling and linking hello takes 6.6 s in C on Linux x64, 12 s in C++ and 11 s in Fortran. `--readytorun=<rid>` precompiles every assembly of the package with ReadyToRun for one host (`scripts/readytorun.sh`, crossgen2 from NuGet); the images keep their IL, so a precompiled package still runs on other hosts, and on its own host the same compiles take 1.9, 4.0 and 3.7 s (C: 1.0 s on macOS ARM64, 2.3 s on Windows x64). It grows the package from 0.7 to 1.1–1.3 GB (a 97 MB universal `.tar.xz` against 261 MB for `linux-x64`). Releases carry one per supported host beside the universal package.

**On Windows.** The compiler gives the tools the paths of the command line in the program's form (`C:\src\a.c` is `/c/src/a.c`, [dev/host-abi.md](../dev/host-abi.md#hosts-other-than-linux)), so Clang's messages and dependency files (`-MD`) name `/c/...` paths. An output without `.dll` is a POSIX launcher and a `.cmd` launcher beside it, which `cmd.exe` and PowerShell run by the name without the extension. The CMake toolchain file names the `.cmd` commands on a Windows host and runs configure probes, `try_run` and tests through `llvmnet --run-launcher`, which starts a launcher's payload: zlib and libxml2 configure, build and pass their tests with Visual Studio's CMake and Ninja from PowerShell. Under Git Bash the POSIX scripts and `make` work as on Linux. Dependency files and messages name `/c/...` paths, which Ninja and IDEs on Windows read as relative to the current drive.

**Not in the portable toolchain:** system mode (Linux x86-64 only, with the native-LLVM packages), native objects and assembly sources (system mode), and NativeAOT publishing, which needs the .NET 10 SDK and the host's native toolchain as described [above](#nativeaot-on-other-hosts).

## The toolchain package

`scripts/package-toolchain.sh <prefix> [--self-contained] [--bundle-llvm]` installs a relocatable Linux x86-64 toolchain, which is also what the release archives contain:

- `bin/llvmnet-clang`, `bin/llvmnet-clang++`, `bin/llvmnet-flang`: the compiler entry points; `bin/llvmnet` selects the frontend from each input's extension.
- `bin/llvmnet-ar`, `bin/llvmnet-ranlib`, `bin/llvmnet-nm`: LLVM's archive and symbol tools for bitcode objects.
- `lib/llvmnet/`: the compiler, its LLVM bridge and the runtime assembly; with `--self-contained`, the compiler's own .NET runtime.
- `libexec/llvm/` (with `--bundle-llvm`): Clang, Flang, `opt`, the archive tools, `libLLVM` and the libraries they load, Clang's builtin headers and Flang's intrinsic modules. Copy a bundled LLVM as a whole: its library aliases must resolve to one library instance.
- `sysroot/dotnet64-v2/`: the portable sysroot (musl with mimalloc, libc++, libc++abi, the OpenMP runtime and flang-rt headers and bitcode archives, and compiler-rt's builtins); `sysroot/system-linux-x64-v1/`: flang-rt built against glibc for system mode; `sysroot/simd128/`: the SIMD128 header.
- `share/llvmnet/`: the documentation, the CMake and Meson files (`cmake/`, `meson/`), the SPEC configuration (`spec/`), the license, the third-party notices and the Debian copyright files of bundled LLVM packages.

A self-contained, bundled installation needs neither `dotnet` nor LLVM on `PATH` to compile (`tests/deployment.sh` checks this with an empty `PATH`), only a compatible Linux x86-64 host with Bash, ordinary shell utilities and the OS libraries LLVM and .NET load. Publishing NativeAOT executables additionally needs the .NET 10 SDK and the target's native toolchain, as above. `LLVMNET_ROOT` names the toolchain root when the compiler is run from elsewhere.

## Release packages

Each release on GitHub carries `llvmnet-<version>-portable.tar.xz` and `.zip` (the [portable toolchain](#the-portable-toolchain) for every .NET 10 host), `llvmnet-<version>-portable-<rid>.tar.xz` for `linux-x64`, `linux-arm64` and `osx-arm64` and `llvmnet-<version>-portable-win-x64.zip` (the same, precompiled for that host), `llvmnet-<version>-linux-x64.tar.xz` (thin: LLVM 22 expected on `PATH`), `llvmnet-<version>-linux-x64-llvm.tar.xz` (LLVM bundled), `llvmnet.<version>.nupkg` (the dotnet tool), `SHA256SUMS`, and a `.sigstore.json` bundle for each file, signed by the release workflow through GitHub's OIDC identity ([dev/release-process.md](../dev/release-process.md)). To verify a download:

```sh
sha256sum -c SHA256SUMS
cosign verify-blob --bundle llvmnet-0.3.0-linux-x64.tar.xz.sigstore.json \
  --certificate-identity-regexp '^https://github.com/<owner>/llvmnet/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com llvmnet-0.3.0-linux-x64.tar.xz
```

The archives are reproducible from the tag: a second build of the same commit gives byte-identical files, and the workflow checks this before signing.
