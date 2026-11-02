# Evidence: TC-21 argv[0] and /proc/self/exe of launcher programs (2026-11-02)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang 22.1.8, starting from commit `0f4f108`; the change is `1d979cb`. Also run on macOS 27.2 ARM64 (`mainmac`, .NET runtime 10.0.14, `/bin/sh` is GNU bash 3.2.57) and Windows 11 x64 (`fluorite`, .NET runtime 10.0.10, Git for Windows bash 5.3.15), with the launcher and payload built on Linux.

## The problem

Building LLVM, Clang and Flang 22 with llvmnet in portable mode (`artifacts/selfhost/`, finding 2 of its `results/REPORT.md`) found every tool confused about its own identity under the JIT. The launcher `-o prog` writes is a `/bin/sh` script running `dotnet prog.llvmnet/program.dll`, so .NET reports the image as `argv[0]` (`Environment.GetCommandLineArgs()`) and the `dotnet` host as the process's executable (`Environment.ProcessPath`, which the host layer returned for `readlink("/proc/self/exe")`). `llvm-ar` could not act as `ranlib`, Clang and Flang could not pick their driver mode from their name, Clang reported `InstalledDir: /usr/share/dotnet` and found no resource directory, Flang found no intrinsic modules, and Flang's driver started `dotnet -fc1` instead of itself. NativeAOT executables were unaffected. The experiment worked around it in its packaging with a .NET apphost per tool name in front of an image hard-linked under that name.

`tests/c/launcher-identity.c` prints `argv[0]`, what `readlink`, `realpath`, `stat`, `lstat`, `access` and `open` see of `/proc/self/exe`, and what a child started with `posix_spawn("/proc/self/exe")` reads there. Built with `0f4f108` as `-o prog` in portable mode:

| Started as | `argv[0]` | `readlink("/proc/self/exe")` | child through `/proc/self/exe` |
| --- | --- | --- | --- |
| `./prog` | `<dir>/prog.llvmnet/program.dll` | `/usr/share/dotnet/dotnet` | `dotnet child`: "Could not execute because the specified command or file was not found", exit 1 |
| `./alias` (a link beside it) | `<dir>/prog.llvmnet/program.dll` | `/usr/share/dotnet/dotnet` | the same |
| `../links/relative` (a link in another directory) | does not start: `dotnet` looks for `<links>/prog.llvmnet/program.dll` and exits 1 | | |

`open("/proc/self/exe")` opened the `dotnet` host. On macOS, which has no `/proc`, `readlink` named the `dotnet` host, and `realpath`, `stat`, `lstat`, `access`, `open` and `posix_spawn` of `/proc/self/exe` failed with `ENOENT` (`0f4f108`'s runtime, measured on `mainmac`).

## Design

Three designs were weighed.

- **.NET apphosts**, as in the experiment. `/proc/self/exe` is then the apphost itself in every mode, system mode included, and the kernel passes `argv[0]` through unchanged, also through `PATH`. But an apphost is a native executable for one runtime identifier: an `-o prog` built on Linux x64 would no longer run on Linux ARM64 or macOS, as the script and the image do today. Writing one needs the SDK's `Microsoft.NETCore.App.Host.<rid>` pack at build time, which a JIT build does not otherwise need (the self-contained toolchain carries only the runtime), and a patched macOS apphost must be signed again. It finds .NET through `DOTNET_ROOT` and the registered install location, not through `PATH` or `LLVMNET_DOTNET`. And .NET still reports the image as `argv[0]`: the experiment hard-linked the image under each tool's name, because a symbolic link to an apphost gives the image's name, so the runtime would need the native command line anyway (`/proc/self/cmdline` on Linux, other calls elsewhere).
- **The shell launcher passes what it knows** (chosen). It keeps the one output form that runs on every Unix host and needs nothing new to build or run. Its limits are what the kernel decides: a script cannot see the name it was started by through `PATH`, because the kernel gives it the path it found instead, and in system mode glibc asks the kernel for `/proc/self/exe`, which names the `dotnet` host.
- **Both**: an apphost form for those two cases. Not done: the base name is the same through `PATH`, which is what programs that choose their behavior by name read, and no program found so far needs system mode's `/proc/self/exe`.

What changed:

- The launcher (`Toolchain.Launcher`) follows symbolic links to itself, as the kernel does for `/proc/self/exe`, so a link in another directory finds the payload, and passes its `$0` and its resolved path in `LLVMNET_ARGV0` and `LLVMNET_EXE`. It runs `readlink` once per link and no other utility; the old one ran `dirname` every time. The script, for `-o prog`:

  ```sh
  #!/bin/sh
  self=$0
  while [ -h "$self" ]; do
      link=$(readlink -- "$self") || exit 1
      case $link in /*) self=$link ;; *) case $self in */*) self=${self%/*}/$link ;; *) self=$link ;; esac ;; esac
  done
  case $self in */*) directory=${self%/*} ;; *) directory=. ;; esac
  root=$(CDPATH= cd -P -- "${directory:-/}" && pwd -P) || exit 1
  export LLVMNET_ARGV0="$0" LLVMNET_EXE="${root%/}/${self##*/}"
  exec "${LLVMNET_DOTNET:-dotnet}" "$root"/'prog.llvmnet/program.dll' "$@"
  ```

- The runtime (`ProcessRuntime.ProgramName` and `Executable`) takes the two variables for `argv[0]` and `/proc/self/exe` and removes them from the .NET environment, from the environment vector it builds for musl and, in system mode, from glibc's (`unsetenv`), so neither the program nor a child it starts (another llvmnet program run as `dotnet other.dll`, say) sees them. Without them, nothing changes: `dotnet program.dll` keeps the image's path and the `dotnet` host, and NativeAOT executables keep their own.
- The host layer serves `/proc/self/exe` as a symbolic link to `Executable` on every host: `readlink` returns it, `stat`, `access` and `open` follow it, `lstat` reports a link, and `posix_spawn` of it starts the executable (before, on Linux, the child that .NET forks found the `dotnet` host there). `/proc/<pid>/exe` with the process's own id is the same link, which musl's `realpath` reaches after reading the `/proc/self` link on Linux. On hosts without `/proc`, `readlink` finds `/proc` and `/proc/<pid>` directories and `/proc/self` a link to the id, so `realpath("/proc/self/exe")` resolves there too.

## Results

`tests/launcher-identity.sh` builds the fixture natively, as a portable launcher, as a system-mode launcher and as a portable NativeAOT executable, each in a tree of the same layout under a path with a space, starts each nine ways and compares the outputs, with the tree's root written `<root>` (a working directory `work/` beside `bin/`):

| Started as | Native `argv[0]` | Launcher `argv[0]` |
| --- | --- | --- |
| `./prog` in `bin/` | `./prog` | `./prog` |
| `./alias` in `bin/` (`alias` → `prog`) | `./alias` | `./alias` |
| `../bin/prog` | `../bin/prog` | `../bin/prog` |
| `<root>/bin/prog` | `<root>/bin/prog` | `<root>/bin/prog` |
| `../links/absolute` (→ `<root>/bin/prog`) | `../links/absolute` | `../links/absolute` |
| `../links/relative` (→ `../bin/prog`) | `../links/relative` | `../links/relative` |
| `../links/chain` (→ `relative`) | `../links/chain` | `../links/chain` |
| `../linked-bin/prog` (`linked-bin` → `bin`) | `../linked-bin/prog` | `../linked-bin/prog` |
| `relative` through `PATH` | `relative` | `<root>/links/relative` |

In every row, for the native build and the portable launcher alike: `readlink` and `realpath` of `/proc/self/exe` give `<root>/bin/prog`, `stat` and `open` reach that file, `lstat` sees a link, `access(X_OK)` succeeds, the child started through `/proc/self/exe` reads `<root>/bin/prog` and exits 0, and neither process has `LLVMNET_ARGV0` or `LLVMNET_EXE` in its environment. The only difference is the last row's `argv[0]`.

- **System mode**: `argv[0]` and the environment match native in the same way, including the `PATH` row. `/proc/self/exe` is the kernel's answer through glibc, `/usr/share/dotnet/dotnet`, and is not compared.
- **NativeAOT** (portable): identical to native in all nine rows, the `PATH` row included.
- **macOS**: the Linux-built launcher and payload, laid out and started the same nine ways, print exactly what the Linux native build prints (after mapping the `PATH` row). Before `/proc` and `/proc/self` were served to `readlink` there, `realpath("/proc/self/exe")` failed with `ENOENT` in every row.
- **Windows**, through Git for Windows' bash, which runs the script (launchers are not a Windows output form, and its `ln -s` copies, so only `./prog`, `../bin/prog` and the absolute path): `argv[0]`, `readlink`, `realpath`, `stat`, `lstat` and `open` match. For the absolute start MSYS hands `LLVMNET_ARGV0` over as `C:/Dev/...`; the runtime accepts `LLVMNET_EXE` in either form. `access(X_OK)` fails, as for every file there (NTFS has no mode bits, [host-abi.md](../dev/host-abi.md)), and `posix_spawn` of the launcher fails with `EIO`, because Windows cannot execute a script.

**Cost.** The launcher's own part, timed with `LLVMNET_DOTNET=true` over 500 starts from bash (the fork and exec of the script included): 3.57 ms before, 2.30 ms after when started directly, 3.51 ms through a symbolic link. The runtime reads and removes two variables.

**Found on the way.** A child started with `posix_spawn`, `system` or `popen` gets the path of its executable as `argv[0]`: `posix_spawn("/bin/sh", {"customname", "-c", ...})` gives the shell `customname` natively and `/bin/sh` under llvmnet, because the host starts children through .NET's `Process`, which sets `argv[0]` itself. It predates this change and is plan item [LC-33](../plan/02-portable-c-library.md).

For the self-hosted LLVM tools this means a plain launcher per tool, with symbolic links for the other names (`clang++` to `clang`, `llvm-ranlib` to `llvm-ar`), now behaves as the experiment's apphosts and hard links did. Not re-run here: that package lives outside the repository.

## Tests

- `tests/launcher-identity.sh` (toolchain, aot): the comparisons above, under Linux, in both modes and as NativeAOT.
- The gate (`tests/run.sh --tag core --tag toolchain --exclude-tag slow`): 100 passed, 0 failed, 0 skipped in 313 s, `launcher-identity` among them.
