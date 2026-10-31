# Workstream TC: Driver, toolchain, packaging and documentation

Prefix `TC`. Owner: unassigned. Milestone: [M7](README.md#m7-toolchain-and-distribution).

## Current state (verified 2026-10-30)

- `Driver.cs` parses options in a hand-written loop of about 100 cases; unknown `-` options are forwarded to Clang/Flang, `-Wl,` options other than `--version`/group markers are rejected. Aliases `llvmnet-clang`, `llvmnet-clang++`, `llvmnet-flang` select the frontend by executable name. Defaults are `-O1 -fno-vectorize -fno-slp-vectorize`; `-c` rejects `.ll`/`.bc`/`.o` inputs.
- Outputs: `.dll` with `runtimeconfig.json` and copied runtime assemblies; a Unix launcher plus `<name>.llvmnet/` payload; or a NativeAOT executable produced by generating a `NativeHost.csproj` and running `dotnet publish`. Windows launchers do not exist.
- `scripts/package-toolchain.sh` builds a relocatable tree, optionally bundling LLVM (about 638 MiB); `tests/deployment.sh` proves compilation with no `dotnet`/`clang`/`flang` on `PATH`. NativeAOT still needs the SDK and a native linker.
- `cmake/llvmnet.cmake`, `meson/llvmnet.ini` and `config/spec/llvmnet.cfg` exist and are exercised by the dependency and SPEC builds.
- Documentation is accurate but is written as dated engineering evidence (`status.md`, `spec-coverage.md`, `toolchain.md`) rather than user documentation.

## Items

### Driver compatibility

- [ ] **TC-01** (L, P0) Table-driven option handling mirroring Clang's option categories: forwarded, handled by llvmnet, ignored with a note (`-fPIC`, `-Wl,-rpath`, `-static`, `-pthread` in portable mode), and rejected with a reason. `--help` and `docs/user/driver.md` are generated from the table. A test feeds every option Clang's own `--help` lists and asserts each one either works or produces a clear error.
- [ ] **TC-02** (M, P0) Link inputs: `-c` on `.ll`/`.bc`/`.o`, `-x ir`, `-l:libfoo.a`, `-Wl,--whole-archive`, archive ordering and rescanning rules documented, duplicate-definition diagnostics naming both files, `-r` of archives, `-nostdlib`/`-nostartfiles`.
- [ ] **TC-03** (S, P0) Decide and document the default optimization level ([ADR-11](decisions.md#adr-11-driver-defaults)); make `-O0` output verifiable and `-O3` fully tested. — **Decided** (2026-10-31, ADR-11): `-O1` without vectorization stays the default. Documentation and the `-O0`/`-O3` tests are open.
- [ ] **TC-04** (S, P0) `-g` (feeds [BE-28](01-compiler-backend.md)), `-fno-exceptions`, `-fno-rtti`, `-fvisibility`, `-std=` defaults per driver alias, `-m` flags rejected in portable mode with a message.
- [ ] **TC-05** (M, P0) Diagnostics pipeline: Clang/Flang output passes through unchanged (colors, `-fdiagnostics-format`), llvmnet errors use the same style (BE-04), exit codes documented, stack traces only with `LLVMNET_TRACE`.
- [ ] **TC-06** (M, P1) Parallel and incremental builds: concurrent `-c` invocations are isolated (already true; add a test), lowered-bitcode caching keyed by hash, and `-MJ` compilation-database output for IDEs.
- [ ] **TC-07** (M, P1) Build-system integration: harden the CMake toolchain file (`try_compile`, `CMAKE_SYSTEM_NAME`, `find_package` behavior), the Meson cross file and an autotools recipe; `llvmnet-config` or pkg-config metadata; CI builds zlib, bzip2, xz, sqlite, lua, zstd, libpng and expat in portable mode through their own build systems ([WG-07](09-workload-gates.md)).
- [ ] **TC-08** (M, P1) Output forms: a self-contained managed publish folder (`--self-contained`), optional ReadyToRun precompilation for startup (`--r2r`), a Windows `.cmd` launcher, and documentation of what must be shipped with each form.
- [ ] **TC-09** (M, P1) Managed library consumption as a first-class feature: define the public surface of `--emit-library` (`CExport` scalar/pointer signatures only in v1), strong-name and version it, generate a C header for consumers, and ship a sample .NET project that references a translated library ([ADR-09](decisions.md#adr-09-portable-abi-evolution)). — **Decided** (2026-10-31, ADR-09): the 1.0 surface is as described; aggregate signatures stay unstable.
- [x] **TC-10** (S, P1) Remove the `managed-host` mode after the ffmpeg artifacts are rebuilt in system and portable mode; untagged bitcode produces an error with guidance ([ADR-04](decisions.md#adr-04-retire-managed-host-mode)). — **Done** (`5b718b7`, ADR-04): `managed-host` is removed with the ffmpeg artifacts rebuilt in both remaining modes, and untagged link inputs are rejected with guidance.

### Packaging and distribution

- [ ] **TC-11** (M, P0) Release artifacts from CI on a tag: `llvmnet-<version>-linux-x64.tar.xz` in thin and bundled-LLVM variants, SHA-256 sums, a signature (minisign or cosign), a `dotnet tool` package for the thin variant, and reproducibility from the tag (FD-05).
- [ ] **TC-12** (M, P0) NativeAOT provisioning: document and script the SDK, ILCompiler package and native linker requirements, support an offline local NuGet feed, and give a precise error when a prerequisite is missing. — **In progress** (2026-10-31, with XP-03): the per-host requirements (MSVC, Xcode, clang, the linux-arm64 cross packages) and `--aot-project` are documented in [toolchain.md](../toolchain.md#nativeaot-on-other-hosts); the offline feed and the upfront prerequisite check are open.
- [ ] **TC-13** (S, P0) Bundle size: include only `clang`, `flang`, `opt`, `llvm-ar/nm/ranlib`, `libLLVM`, the resource directory and Flang `.mod` files; strip binaries; measure before and after.
- [ ] **TC-14** (M, P1) `llvmnet doctor`: checks LLVM version, .NET runtime version, missing shared libraries for bundled LLVM, sysroot completeness, and NativeAOT prerequisites.
- [ ] **TC-15** (M, P2) Native package channels (Debian, Homebrew, winget). Post-1.0 unless trivial.

### Documentation

- [ ] **TC-16** (L, P0) Restructure `docs/`: `docs/user/` (getting started, runtime modes and ABI contract, driver reference generated by TC-01, linking managed and native code, deployment, limitations, troubleshooting), `docs/dev/` (architecture, host ABI, exception bridge, adding a host function, testing, release process, versioning), `docs/evidence/` (dated status, SPEC and performance records moved from the current top-level files). Rewrite `README.md` for users and keep every claim tied to CI evidence (FD-22).
- [ ] **TC-17** (S, P0) One limitations page generated from BE-02 and the host layer's ENOSYS table, replacing the prose lists in `status.md`.
- [ ] **TC-18** (S, P1) `examples/`: a C library consumed from C#, a C++ application, a Fortran application, a CMake project and a Meson project, each built in CI.
