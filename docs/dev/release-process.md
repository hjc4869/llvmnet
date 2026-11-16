# Release process

A release is a tag, a `CHANGELOG.md` section, the artifacts the tag's workflow builds, and an updated status in the plan. Versions follow [versioning.md](versioning.md): SemVer for the compiler, with the ABI tags versioned separately.

## Cutting a release

1. Make sure the gate is green on `master` and the nightly jobs have been green for the period the milestone's exit criterion names ([plan/README.md](../plan/README.md)).
2. In `Directory.Build.props`, set `VersionPrefix` to the release version and `VersionSuffix` to empty.
3. Move the `Unreleased` section of `CHANGELOG.md` under a `## [<version>] - <date>` heading; the release notes are taken from it.
4. Commit, tag `v<version>` and push the tag. The `release` workflow (`.github/workflows/release.yml`) only builds and packages, since the gate has tested the commit: the Linux packages and the dotnet tool in one job, the portable toolchain in another, the LLVM tools as NativeAOT executables on a runner of each host, and each host's precompiled portable package in parallel jobs; then it creates the GitHub release with the artifacts and the notes attached. `tests/release.sh` in the gate checks that the archives are reproducible.
5. To bootstrap the next release's LLVM from this one, point the `llvmnet-bootstrap` row of `scripts/sources.lock` at this release's `llvmnet-<version>-portable-linux-x64.tar.xz` and its SHA-256 from `SHA256SUMS` ([below](#the-portable-toolchain-in-the-workflow)).
6. Bump `VersionPrefix` to the next minor version and restore `VersionSuffix` to `dev`.

## The artifacts

`scripts/release-artifacts.sh <out-dir> [version]` builds, for a commit:

| File | Contents |
| --- | --- |
| `llvmnet-<version>-linux-x64.tar.xz` | The thin toolchain: the compiler with its own .NET runtime (`--self-contained`), the LLVM bridge, the sysroots, the documentation, the licenses; LLVM 22 is expected on `PATH` |
| `llvmnet-<version>-linux-x64-llvm.tar.xz` | The same with Clang, Flang, `opt`, the archive tools, `libLLVM` and their libraries, the builtin headers and the Flang modules bundled (`--bundle-llvm`) |
| `llvmnet.<version>.nupkg` | The dotnet tool: `dotnet tool install -g llvmnet --add-source <dir>` installs the `llvmnet` command with the bridge and the sysroots; LLVM 22 on `PATH` |
| `llvmnet-<version>-portable.tar.xz`, `.zip` | The portable toolchain for every .NET 10 host: the framework-dependent compiler on `LlvmNet.Llvm.dll`, the llvmnet-built Clang, Flang, `opt`, `llvm-ar` and `llvm-nm`, the sysroots and the `bin/` commands for Unix and Windows (variant `portable`; [user/deployment.md](../user/deployment.md#the-portable-toolchain)) |
| `llvmnet-<version>-portable-<rid>.tar.xz` (`linux-x64`, `linux-arm64`, `osx-arm64`), `llvmnet-<version>-portable-<rid>.zip` (`win-x64`, `win-arm64`) | The same for that host only: the LLVM tools as NativeAOT executables, the compiler precompiled with ReadyToRun (variants `portable-<rid>`) |
| `SHA256SUMS` | The sums of the files above |
| `VERSION` | The version the file names carry, which the workflow names the release by (not attached) |

The artifacts are reproducible: `tar` sorts the names, fixes the owner to 0 and every timestamp to the commit's (`SOURCE_DATE_EPOCH`), and drops the atime and ctime headers; `xz` uses a fixed block size so the thread count does not change the stream; the .NET build is deterministic and the version string comes from the commit; the tool package is rewritten with the commit time in its zip entries, because NuGet stamps them with the build time. `tests/release.sh` in the gate builds the artifacts twice and compares the sums. A release is built from a committed tree: the package copies `docs/`, so an uncommitted edit there changes the archive.

### The portable toolchain in the workflow

The `portable` job builds LLVM, Clang, MLIR and Flang with llvmnet ([scripts/selfhost/README.md](../../scripts/selfhost/README.md)): `configure.sh`, `build-library.sh` and `build-tools.sh`, then the universal package (`release-artifacts.sh`'s `portable` variant), which it uploads with the NativeAOT projects of its tools (`scripts/aot-tools.sh projects`). It is a bootstrap build (XP-11): the release pinned as `llvmnet-bootstrap` in `scripts/sources.lock` compiles LLVM's sources and the host tools of LLVM's own build (TableGen) with its package precompiled for `linux-x64`, which `fetch-sources.sh` downloads and checks and `scripts/selfhost/bootstrap.sh` uses as it is: its LLVM tools are NativeAOT executables (a universal package's run under the JIT, and `bootstrap.sh` would first precompile it, about 15 minutes on 4 cores). The tag's compiler links and translates the library and the tools on that release's LLVM, so the job uses no LLVM of the runner's for them. A manual run with `llvm: native` builds them with the tag's compiler on Debian's LLVM 22 instead, the way to recover when the pinned release cannot bootstrap the commit (a different portable ABI tag, or a runtime function its translated code calls that the commit changed). The compiler itself and the sysroots are still built with Debian's LLVM 22 in both cases, as in the `artifacts` job.

A cold build is about 5,300 objects and does not fit one 6-hour run on a 4-core runner, so the job restores the newest cached build tree of its kind (`artifacts/selfhost-build/llvm-project` and `llvm`, keyed by `bootstrap` or `native`, `scripts/selfhost/` and `sources.lock`) and saves it even when it times out: rerun it until it completes, or set the repository variable `LLVMNET_PORTABLE_RUNNER` to a larger runner. With a warm cache a release costs the links and translations of the five tools and the library (the library twice in a bootstrap build), and the packaging. Both build jobs restore the sysroots from a cache keyed by their inputs, which `build-sysroot.sh` checks again before using them.

NativeAOT compiles only for the operating system it runs on, so the `aot-tools` jobs publish the tools' projects on a runner of each host (`scripts/aot-tools.sh publish`): Ubuntu 24.04 for `linux-x64` and `linux-arm64`, Windows Server 2025 for `win-x64`, Windows 11 ARM for `win-arm64` and macOS 15 for `osx-arm64`, with the .NET 10 SDK and the runner's C toolchain (clang and zlib on Linux, MSVC, Xcode). Clang takes about 6 minutes and 5 GB on 4 cores. The `portable-precompiled` jobs, one per host on Linux, unpack the universal package and make the host's package with `LLVMNET_PORTABLE_UNIVERSAL`, `LLVMNET_AOT_TOOLS` and the `portable-<rid>` variant: the tools' executables replace their payloads and launchers, and crossgen2 precompiles the compiler, `LlvmNet.Llvm.dll` and the runtime. `workflow_dispatch` with `portable: false` skips the portable jobs. The `release` job attaches every file to a GitHub release, one asset per file: the release of the pushed `v*` tag, with its `CHANGELOG.md` section as the notes, or for a manual run a prerelease `v<version>.<run number>` (the version without its `+<commit>`) created at the built commit. The jobs pass their files to it as workflow artifacts kept for a day. The files are not signed (the owner's decision of 2026-11-15); `SHA256SUMS` lists their sums.

The `win-arm64` package has not run on a Windows ARM64 host.

The workflow runs no tests. Before a release, check a portable toolchain on the hosts with `tests/portable-toolchain/`: `make-kit.sh` on Linux, then the kit's `run.sh <kit> <toolchain>` on each host, and `libraries.sh` where CMake and Meson are installed ([evidence/2026-11-15-portable-toolchain.md](../evidence/2026-11-15-portable-toolchain.md)).

## Verifying a download

```sh
sha256sum -c --ignore-missing SHA256SUMS
```

The sums detect a damaged or incomplete download. The files are not signed, so the sums are only as trustworthy as the release page they come from.

## Publishing the tool to nuget.org

The workflow attaches the `.nupkg` to the release and does not push it to nuget.org, which needs an API key held by the owner: `dotnet nuget push llvmnet.<version>.nupkg --source https://api.nuget.org/v3/index.json --api-key <key>`.
