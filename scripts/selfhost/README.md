# Building LLVM with llvmnet

These scripts build the parts of LLVM 22 that the llvmnet toolchain runs, with llvmnet itself in portable mode, so the
whole toolchain is .NET code that runs on every .NET 10 host (XP-04). They produce:

- `LlvmNet.Llvm.dll`: the compiler's in-process LLVM. LLVM's C API and llvmnet's bridge (`native/*.cpp`) are linked
  with `-shared --embed-libc` into one managed library. The compiler calls it instead of the host's `libLLVM-22` and
  `libllvmnet-llvm` when it sits beside `llvmnet.dll` or `LLVMNET_LLVM` names it (`src/LlvmNet.Compiler/LlvmManaged.cs`).
- `clang`, `flang`, `opt`, `llvm-ar` and `llvm-nm` as portable JIT images, staged as a toolchain's `libexec/llvm`: a
  launcher pair (a POSIX script and a `.cmd`) for each name the compiler and the `bin/` commands call, Clang's resource
  headers, and Flang's intrinsic modules made by the built flang.

`scripts/package-toolchain.sh <prefix> --portable [--precompile=<rid>]` puts them together with the compiler and the
sysroots, and `scripts/release-artifacts.sh` with `LLVMNET_RELEASE_VARIANTS="portable portable-<rid>..."` archives the
result. The release workflow runs all of it (`.github/workflows/release.yml`, job `portable`), as a bootstrap build.

## Steps

```sh
bash scripts/ensure-built.sh                 # the compiler (on the host's LLVM) and the sysroots
scripts/fetch-sources.sh llvm-project        # the pinned source (scripts/sources.lock)
bash scripts/selfhost/configure.sh           # one build tree for the library and the tools
bash scripts/selfhost/build-library.sh       # <work>/library/LlvmNet.Llvm.dll
bash scripts/selfhost/build-tools.sh         # <work>/stage (libexec/llvm)
bash scripts/package-toolchain.sh /tmp/llvmnet-portable --portable
```

`<work>` is `artifacts/selfhost-build` (`LLVMNET_SELFHOST_DIR`). `JOBS` sets the parallelism. The toolchain that compiles
LLVM is this checkout, on the host's LLVM 22, by default; `LLVMNET_SELFHOST_TOOLCHAIN` names a portable toolchain
instead, which makes it a bootstrap build:

```sh
export LLVMNET_SELFHOST_TOOLCHAIN="$(bash scripts/selfhost/bootstrap.sh)"   # the pinned release, precompiled for this host
```

## Bootstrap builds

A release's LLVM is built by the previous release's portable toolchain (XP-11), so it depends on no LLVM of the host's:
`llvmnet-bootstrap` in `scripts/sources.lock` pins a release's universal package by its SHA-256, and `bootstrap.sh`
precompiles it for this host in `<work>/bootstrap` as the release's own precompiled package is made
(`scripts/precompile-toolchain.sh`, about 15 minutes on 4 cores). Its tools are then NativeAOT executables: one LLVM
object costs about 1.3 times the CPU time of Debian's Clang with them, against 4.6 times with the ReadyToRun tools of a
release before XP-14 and more under the JIT. The path stays the same when the pin changes, so the build tree keeps its
objects, as after a compiler change.

- **Compiling.** The portable toolchain's `llvmnet-clang++` compiles LLVM's sources, as this checkout's would, with its
  own Clang, `opt` and sysroot headers. The host tools LLVM runs during its build (TableGen) come from its
  NATIVE sub-build, which the toolchain's `clang-22` compiles as an ordinary native compiler: an llvmnet-built Clang
  emits x86-64 code and links with the system's `ld` and libstdc++ like any Clang.
- **Linking.** The library and the tools are linked, and so translated to CIL, by this checkout's compiler, so they
  match the runtime and the compiler they ship with. That compiler needs an in-process LLVM with this checkout's bridge
  (`native/*.cpp` changes often), which nothing provides yet: `build-library.sh` first links the library with the
  portable toolchain (`<work>/library/stage1/`), then again with this checkout's compiler running on that first library
  and on the portable toolchain's Clang, Flang and `opt` (`LLVMNET_LLVM`, `LLVMNET_CLANG`, `LLVMNET_FLANG`,
  `LLVMNET_OPT`). `build-tools.sh` links each tool the same way, running ninja's own link command with this checkout's
  driver in place of the portable toolchain's.
- **Limits.** The objects carry the portable toolchain's ABI stamp and were compiled against its sysroot headers, so
  the release pinned must have the same portable ABI tag (`dotnet64-v2`) as this checkout. The first library is
  translated by the pinned release and runs in this checkout's compiler, so a change that removes or changes a runtime
  function the release's translated code calls stops the bootstrap at the library's second link, with a
  missing-method error; build that release's LLVM the other way (the release workflow's `llvm: native`) and pin the
  result for the next one.

## How LLVM is configured

- **Source.** The pinned llvm-project with the patches in `patches/`, copied to `<work>/llvm-project` once per change of
  the lock entry or the patches. The only patch replaces `__builtin_frame_address(0)` in `llvm::getStackPointer()`,
  which llvmnet does not translate, with a local's address.
- **Platform macros.** The portable target predefines no OS macros, but its programs run on musl over the host layer's
  Linux system calls on every host ([ADR-14](../../docs/plan/decisions.md)), so LLVM is compiled with `__linux__`,
  `__unix__` and their spellings and takes its POSIX paths (`/proc/self/exe`, which the host layer serves everywhere).
- **Visibility.** `-fvisibility=hidden` with `LLVM_ENABLE_LLVM_C_EXPORT_ANNOTATIONS` leaves the C API as the only
  default-visibility functions, which the library exports as `libLLVM` does. Executables are internalized anyway.
- **Generated tools.** In a bootstrap build all of TableGen comes from LLVM's NATIVE sub-build, compiled by the
  portable toolchain's Clang. Otherwise `llvm-tblgen` and `clang-tblgen` (`clang-tools-22`) are the host's LLVM 22, and
  the NATIVE sub-build makes the rest (`mlir-tblgen`, `llvm-min-tblgen`) with `clang-22`. `build-library.sh` takes the
  libraries the bridge needs from the tree's CMake exports, not from an `llvm-config`, which a cross build's NATIVE tree
  cannot answer for.
- **Off:** precompiled headers (llvmnet cannot produce them), Flang's runtime (the sysroot has llvmnet's flang-rt),
  zlib, zstd, libxml2, plugins, the static analyzer, tests and docs. `LLVM_APPEND_VC_REV=OFF` keeps the producer
  string of the bitcode the library writes at `22.1.8`.

## Rebuilding

The build tree is incremental. ninja does not see the llvmnet compiler, so `build-tools.sh` relinks the five images
every time and `build-library.sh` always relinks the library. `build-sysroot.sh` gives the sysroot's headers new times
whenever it rebuilds; the scripts record the header hash the tree was compiled against (`.llvmnet-headers`) and give
unchanged headers an older time, so a compiler-only change costs the links (about 10 minutes of CPU) rather than the
5,300 objects (about 6 hours). CI caches `<work>/llvm-project`, `<work>/llvm` and `<work>/bootstrap` on that basis.

## Costs (Linux x86-64, 2026-11-15; bootstrap 2026-11-16)

- About 5,300 objects at `-O3`, five to six seconds each; the library's 1,562 come first.
- Links with translation to CIL: clang about 3 minutes and 7 GB, flang about 4.5 minutes, opt about 1 minute.
- Images: clang 196 MB, flang 287 MB, opt 96 MB, `LlvmNet.Llvm.dll` 27 MB; the portable package is 733 MB (97 MB as
  `.tar.xz`), and precompiled for one host (`scripts/precompile-toolchain.sh`) 478 MB (116 MB as `.tar.xz`): NativeAOT
  publishes the five tools in 11 to 14 minutes on 4 to 8 cores, clang alone in about 6 minutes and 5 GB.
- Bootstrap build: one object at about 1.3 times the CPU time of the build on Debian's LLVM with the prepared toolchain
  (`bootstrap.sh`, 14 minutes on 4 cores); the library's two links about 3.5 minutes; the tools' links on the managed
  in-process LLVM up to 12 GB, against 9 GB on Debian's ([evidence](../../docs/evidence/2026-11-16-bootstrap-and-native-tools.md)).
