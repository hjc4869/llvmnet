# Evidence: M6 cross-platform execution (2026-10-31)

Build host: Linux x86-64 (Debian 13), .NET SDK 10.0.403, LLVM/Clang/Flang 22.1.8. The other hosts, reached over SSH, with a subdirectory of their development folders for everything below:

| Host | System | .NET | Native toolchain for NativeAOT |
| --- | --- | --- | --- |
| Windows x64 | Windows 11 Enterprise 10.0.28000, 32 logical CPUs | SDK 10.0.301, runtime 10.0.10 (an 11.0 release candidate is also installed; the bundle's `global.json` keeps publishing on 10.0); a 32-bit (x86) .NET 10.0.9 runtime for the XP-06 check | Visual Studio 2026 Enterprise, MSVC 14.51, Windows SDK 10.0.28000 |
| macOS ARM64 | macOS 27.2 on an Apple M6, 12 cores | SDK 10.0.403 installed with `dotnet-install.sh` under the development folder (no system-wide .NET) | Xcode 27, Apple clang 21 |
| Linux ARM64 | the `mcr.microsoft.com/dotnet/runtime:10.0` container for linux/arm64 under qemu-user on the build host (JIT); qemu-user with Debian's `libc6-arm64-cross` sysroot for cross-published executables | runtime 10.0.14 | cross-published from the build host: `clang`, `libc6-dev-arm64-cross`, `libgcc-16-dev-arm64-cross`, `binutils-aarch64-linux-gnu` |

Every number was produced by a command in this repository; the per-host result files are the `results.tsv` written by `tests/xp/run.sh`.

## Host interface conformance on each host

`tests/c/host-errors.c` (the fixture of `tests/host.sh`, 188 checks of results and errno values) compiled in portable mode on Linux and run on each host, compared with the native Linux output:

| Host | Before this work | After |
| --- | --- | --- |
| macOS ARM64 | 188 of 188 identical | 188 of 188 |
| Linux ARM64 (container, running as root) | 185 of 188: the three checks that need an unsearchable or unreadable directory, which root can always read | same |
| Windows x64 | 178 of 188 and a crash: `getcwd` and `realpath` failed because `C:\...` does not start with `/`; a link to a directory was a file link; `system` and `popen` failed without `/bin/sh` and the fixture's `pclose(NULL)` then faulted; plus the differences that remain | 183 of 188 |

What remains different on Windows is what NTFS and Windows have no equivalent for, documented in [dev/host-abi.md](../dev/host-abi.md#hosts-other-than-linux): `stat` reports `0644` after `chmod 0600` (no mode bits), an unsearchable or unreadable directory cannot be made with mode bits (three checks), and the time-zone abbreviation from a generated zoneinfo image is numeric (`+02` for CEST, as LC-09 documents). The fixes: a program-visible path form for drives (`/c/Users/...`), `/tmp` as the temporary directory, directory links created as directory links, `sh.exe` from Git for Windows for `/bin/sh`, Win32 error codes mapped to errno values, and the console switched to UTF-8 (`src/LlvmNet.Runtime/Host/HostPaths.cs`, `HostConsole.cs`, and the changes in `HostSyscalls.cs`, `HostFileSystem.cs`, `HostProcess.cs`).

## The cross-platform bundle (XP-01, XP-02, XP-03)

`tests/xp/build.sh` compiles the 51 fixtures of `tests/xp/fixtures.tsv` (26 C, 12 C++, 13 Fortran: control flow, memory, varargs, 80-bit floats, under-aligned access, atomics and contended threads, setjmp/longjmp, stdio and file descriptors, `std::filesystem`, exceptions and `std::exception_ptr`, thread-local destructors, futures, termination order, Fortran I/O, namelists, unformatted and derived-type records, random sequences) natively and in portable mode on Linux x64, records what the native build prints and returns, and writes a relocatable NativeAOT project for 9 of them. `tests/xp/run.sh` runs the images in a fresh directory on the host and compares stdout and the exit status; `--aot` publishes the projects with the host's SDK and toolchain and runs the executables. `tests/xp.sh` (core tag) does the Linux x64 run; the `xp` CI jobs do the others from the same bundle.

| Host | JIT | NativeAOT | Notes |
| --- | ---: | ---: | --- |
| Linux x64 (build host) | 51 of 51 | 9 of 9 (`tests/xp-aot.sh`) | |
| Windows x64 | 51 of 51 | 9 of 9 | published with MSVC in about six minutes, three at a time |
| macOS ARM64 | 51 of 51 | 9 of 9 | published with Xcode |
| Linux ARM64 | 51 of 51 (container) | 9 of 9, cross-published on the build host (XP-05) and run under qemu-user | |

Fixtures that were considered and left out, because their output depends on the host rather than on llvmnet: `posix.c` (checks `chmod` bits), `rounding.c` (rounding modes, which portable mode fixes), `complex-runtime.c` (compares with glibc's complex functions, which differ from musl's by one ulp), `openmp.c` and `concurrent.f90` (need the native OpenMP headers and runtime on the build host), `random-device.cpp` (`std::random_device` tokens) and `descriptor-stack.c` (links flang-rt natively).

## Unsupported hosts (XP-06)

The emitted entry point now asks the runtime whether the host can run the image before the image's own type initializer runs. On the Windows host's 32-bit runtime:

```
> "C:\Program Files (x86)\dotnet\dotnet.exe" smoke.dll
llvmnet: llvmnet programs need a 64-bit .NET runtime; this is a 32-bit X86 runtime (.NET 10.0.9). Run the program with the x64 or Arm64 .NET 10 runtime ('dotnet --list-runtimes' shows the installed ones).
(exit status 1)
```

Before, the same run ended in a `TypeInitializationException` stack trace with the message inside it. Mono, big-endian hosts and runtimes before .NET 10 get their own lines (`tests/LlvmNet.UnitTests/AbiContractTests.cs`). Every image's `runtimeconfig.json` now says `"rollForward": "Minor"`, so the framework band is .NET 10 and a host with only .NET 11 reports the missing framework instead of running untested.

## Cross-publishing to linux-arm64 (XP-05)

With Debian's cross packages installed on the Linux x64 host, `llvmnet --runtime=portable --nativeaot --runtime-id linux-arm64 tests/c/threads.c -pthread -o threads-arm64` produces an aarch64 ELF executable in 10 seconds; the generated project selects `aarch64-linux-gnu-objcopy` for the strip step, which the SDK would otherwise run with the host's `objcopy`. The executable prints the native output under qemu-user and in the arm64 container.

## A dependency found on the way (XP-07)

Under qemu-user with only the C library in the sysroot, every portable image aborted at startup with `No usable version of libssl was found`: the start vector's `AT_RANDOM` bytes came from `RandomNumberGenerator`, which loads OpenSSL on Linux. `getrandom`, `/dev/urandom` and `AT_RANDOM` now read the host's `/dev/urandom` on Unix hosts (`src/LlvmNet.Runtime/Host/HostRandom.cs`); Windows keeps the system generator. A portable program on Linux therefore needs only the C library, as a NativeAOT executable's `ldd` shows.

## Console signals on Windows

Console events reach a portable program through .NET's console handler (`PosixSignalRegistration`, the same path `tests/host.sh` checks with `SIGTERM` on Linux), which maps Ctrl-C to `SIGINT` and Ctrl-Break to `SIGQUIT`. A PowerShell script on the Windows host started a program with handlers for `SIGINT`, `SIGQUIT`, `SIGTERM` and `SIGHUP` on the shell's console and called `GenerateConsoleCtrlEvent`: Ctrl-Break produced `received signal 3` and a normal exit. The Ctrl-C event was not observed, because PowerShell's own handling of the event stopped the script before the program's output could be read; it travels the same code path (`CTRL_C_EVENT` to `PosixSignal.SIGINT` in `HostProcess.UpdateRegistration`) and remains to be checked from an interactive console.

## Test gates

`tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` after the runtime and compiler changes: 83 passed, 0 failed in 483 s, including the new `xp` test (158 s: the bundle build and the Linux x64 JIT run). `tests/run.sh xp-aot`: 9 of 9 NativeAOT executables. `dotnet test tests/LlvmNet.UnitTests`: 87 passed, with the new `HostPathsTests` (the Windows path forms, checked on Linux through the testable overloads) and `AbiContractTests`. The host-interface fixture `tests/host.sh` is part of the core run and still matches native on Linux after the path-resolution changes.

The per-host result files of the final bundle (`llvmnet 816d455-dirty`, built 2026-10-31T07:35Z): `windows-x86_64` 60 passed, `macos-arm64` 60 passed, `linux-aarch64` 51 passed (JIT), `linux-x86_64-linux-arm64` 9 passed (cross-published NativeAOT), `linux-x86_64` 51 (JIT) and 9 (NativeAOT) passed.
