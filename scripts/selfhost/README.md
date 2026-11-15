# Building LLVM with llvmnet

These scripts build the parts of LLVM 22 that the llvmnet toolchain runs, with llvmnet itself in portable mode, so the
whole toolchain is .NET code that runs on every .NET 10 host (XP-04). They produce:

- `LlvmNet.Llvm.dll`: the compiler's in-process LLVM. LLVM's C API and llvmnet's bridge (`native/*.cpp`) are linked
  with `-shared --embed-libc` into one managed library. The compiler calls it instead of the host's `libLLVM-22` and
  `libllvmnet-llvm` when it sits beside `llvmnet.dll` or `LLVMNET_LLVM` names it (`src/LlvmNet.Compiler/LlvmManaged.cs`).
- `clang`, `flang`, `opt`, `llvm-ar` and `llvm-nm` as portable JIT images, staged as a toolchain's `libexec/llvm`: a
  launcher pair (a POSIX script and a `.cmd`) for each name the compiler and the `bin/` commands call, Clang's resource
  headers, and Flang's intrinsic modules made by the built flang.

`scripts/package-toolchain.sh <prefix> --portable [--readytorun=<rid>]` puts them together with the compiler and the
sysroots, and `scripts/release-artifacts.sh` with `LLVMNET_RELEASE_VARIANTS="portable portable-<rid>..."` archives the
result. The release workflow runs all of it (`.github/workflows/release.yml`, job `portable`).

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
LLVM is this checkout by default (`LLVMNET_SELFHOST_TOOLCHAIN` names another, such as an installed portable toolchain).

## How LLVM is configured

- **Source.** The pinned llvm-project with the patches in `patches/`, copied to `<work>/llvm-project` once per change of
  the lock entry or the patches. The only patch replaces `__builtin_frame_address(0)` in `llvm::getStackPointer()`,
  which llvmnet does not translate, with a local's address.
- **Platform macros.** The portable target predefines no OS macros, but its programs run on musl over the host layer's
  Linux system calls on every host ([ADR-14](../../docs/plan/decisions.md)), so LLVM is compiled with `__linux__`,
  `__unix__` and their spellings and takes its POSIX paths (`/proc/self/exe`, which the host layer serves everywhere).
- **Visibility.** `-fvisibility=hidden` with `LLVM_ENABLE_LLVM_C_EXPORT_ANNOTATIONS` leaves the C API as the only
  default-visibility functions, which the library exports as `libLLVM` does. Executables are internalized anyway.
- **Generated tools.** TableGen comes from the host's LLVM 22 (`llvm-tblgen`, `clang-tblgen` from `clang-tools-22`);
  `mlir-tblgen` is built natively by LLVM's NATIVE sub-build with `clang-22`.
- **Off:** precompiled headers (llvmnet cannot produce them), Flang's runtime (the sysroot has llvmnet's flang-rt),
  zlib, zstd, libxml2, plugins, the static analyzer, tests and docs. `LLVM_APPEND_VC_REV=OFF` keeps the producer
  string of the bitcode the library writes at `22.1.8`.

## Rebuilding

The build tree is incremental. ninja does not see the llvmnet compiler, so `build-tools.sh` relinks the five images
every time and `build-library.sh` always relinks the library. `build-sysroot.sh` gives the sysroot's headers new times
whenever it rebuilds; the scripts record the header hash the tree was compiled against (`.llvmnet-headers`) and give
unchanged headers an older time, so a compiler-only change costs the links (about 10 minutes of CPU) rather than the
5,300 objects (about 6 hours). CI caches `<work>/llvm-project` and `<work>/llvm` on that basis.

## Costs (Linux x86-64, 2026-11-15)

- About 5,300 objects at `-O3`, five to six seconds each; the library's 1,562 come first.
- Links with translation to CIL: clang about 3 minutes and 7 GB, flang about 4.5 minutes, opt about 1 minute.
- Images: clang 295 MB, flang 405 MB, opt 145 MB, `LlvmNet.Llvm.dll` 27 MB; precompiled for one host with
  `scripts/readytorun.sh` (2 minutes) the toolchain grows from 0.9 to 1.5 GB.
