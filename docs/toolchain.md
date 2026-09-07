# Deployable Toolchain and ABI Modes

## Installation

The Linux distribution has conventional compiler entry points:

- `bin/llvmnet-clang`, `bin/llvmnet-clang++`, `bin/llvmnet-flang`.
- `bin/llvmnet-ar`, `bin/llvmnet-ranlib`, `bin/llvmnet-nm` for bitcode objects.
- `bin/llvmnet` for extension-based frontend selection.
- `lib/llvmnet/` for the compiler and its managed runtime libraries.
- `libexec/llvm/` for optional bundled LLVM, Clang, Flang, builtin headers, and intrinsic Fortran modules.
- `sysroot/dotnet64-v1/` for versioned portable headers and C++ support bitcode.
- `share/llvmnet/` for documentation, SPEC configuration, and redistribution notices.

Build a thin installation or bundle the compiler's .NET runtime and LLVM tools:

```sh
bash scripts/package-toolchain.sh /path/to/prefix
bash scripts/package-toolchain.sh /path/to/prefix --self-contained --bundle-llvm
export PATH=/path/to/prefix/bin:$PATH
```

The bundled distribution was tested after relocation and with `dotnet`, `clang`, and `flang` absent from `PATH`. It still requires a compatible Linux x86-64 host, Bash, ordinary shell utilities, and the OS libraries needed by LLVM/.NET. `--self-contained` refers to the compiler process, not an entirely static Linux binary. Do not mix independently copied LLVM library images; its aliases must resolve to one library instance.

NativeAOT publishing additionally requires the .NET 10 SDK/NativeAOT packages and the target's native linker/development libraries. They are not part of the compiler bundle yet. A produced NativeAOT executable does not need an installed `dotnet` command.

The package builder obtains pinned libc++ 22.1.8 headers from the Debian package repository without administrator privileges, preserves their notices, and builds the owned support sources as ABI-tagged LLVM bitcode. This is an initial libc++ subset, not the complete libc++ library: containers/algorithms and the tested exception classes work; localization, filesystem, timezone database, and many out-of-line facilities are not provided.

## Driver Contract

Frontend flags such as `-O0` through `-O3`, `-g`, `-std=`, `-D`, `-U`, `-I`, `-isystem`, warning flags, `-pthread`, and Fortran module flags are forwarded to Clang/Flang where compatible. `-c`, `-E`, `-S -emit-llvm`, `-M`, `-MM`, `-MD`, `-MMD`, `-MF`, `-MT`, joined/separate `-o`, response files, and relocatable `-r` are implemented. `--version`, `-v`, and compiler resource queries report the actual selected frontend.

The compiler aliases select their language even when a filename alone is ambiguous. `llvmnet-clang++` uses clang's C++ driver mode; `llvmnet-flang` uses Flang. Defaults still favor scalar O1 LLVM IR with auto-vectorization disabled. This is not full clang driver compatibility: arbitrary `-Wl,` options, native `.o` files/static archives, complete LTO policy, and unsupported backend operations are rejected or remain unimplemented. `-pipe` is accepted; bitcode is inherently used for linking, so accepted `-flto` spellings do not invoke clang's native LTO linker.

Output selection is independent of ABI selection:

| Choice | Result |
| --- | --- |
| `-o program.dll` | CIL PE assembly, runtimeconfig, and runtime dependencies |
| `-o program` or no `-o` | Unix executable launcher plus `program.llvmnet/` payload; default name `a.out` |
| `-shared` / `--emit-library` | Managed CIL library, not a native shared object |
| `--nativeaot -o program` | RID-specific NativeAOT executable |
| `--emit-llvm -o program.bc` | Linked LLVM bitcode |
| `-r -o combined.o` | Relocatable linked bitcode |

NativeAOT options include `--runtime-id <rid>`, `--aot-optimize Balanced|Speed|Size`, and `--aot-debug`. Invalid combinations such as `--nativeaot -c` are errors. The current system ABI requires `linux-x64`. Other portable NativeAOT RIDs require an appropriate build host/toolchain and have not been executed in this environment.

## Runtime Modes

Choose the same mode for every source, object, archive, and managed reference in one program. LLVM module flags and managed assembly metadata carry the ABI identity. Incompatible combinations fail at link time, and incompatible loaded modules cannot share runtime state. Untagged legacy LLVM input is accepted only in `managed-host` mode.

### System ABI

```sh
llvmnet-clang --runtime=system -c source.c -o source.o
llvmnet-clang --runtime=system source.o -lm -pthread --nativeaot -o program
llvmnet-clang --runtime=system source.c --system-library /path/libexample.so -o program.dll
```

Identity: `system-linux-x64-v1`. This mode intentionally depends on the native Linux x86-64 SysV ABI and the selected host headers/libraries. C stdio, allocation, errno, math, file APIs, native data exports, and most pthread calls use real system libraries.

LLVM generates a small native ABI bridge for each imported call signature. CIL passes a byte argument record; LLVM emits the actual native call, including varargs, register-classified structs, `byval`, and `sret`. This avoids guessing complex native call shapes in P/Invoke signatures. Ordinary C callbacks use unmanaged-callable CIL thunks; only non-variadic scalar callback signatures are currently supported.

JIT output includes a generated native bridge shared library. NativeAOT embeds that bridge into the executable using static native inputs and direct imports, allowing SPEC to stage one executable. User-supplied dynamic libraries remain dynamic dependencies. Native globals resolve through the selected native library, not synthetic managed copies.

Native threads are real pthreads. A small adapter wraps thread start and TLS destructors to keep callbacks within .NET's thread lifetime, including NativeAOT. Cancellation, `pthread_exit`, hardware-fault signal recovery, and arbitrary foreign-thread TLS destructor interactions are not complete. C++ unwinding and Fortran-specific runtime calls still use managed compatibility adapters; this mode is not a complete native C++/Fortran runtime ABI replacement.

### Portable .NET ABI

```sh
llvmnet-clang --runtime=portable source.c -o portable.dll
llvmnet-clang++ --runtime=portable -std=c++17 source.cpp -o portable-cpp.dll
llvmnet-flang --runtime=portable source.f90 -o portable-fortran.dll
dotnet portable.dll
llvmnet-clang --runtime=portable --nativeaot source.c -o native-program
```

Identity: `dotnet64-v1`. Source compiles against toolchain-owned headers with `-nostdinc`; libc++ wrappers precede those C headers. Host OS/CPU feature macros are absent. The LLVM target is a fixed internal `x86_64-unknown-none` layout used to define the ABI, not to generate x86 machine instructions for CIL output. ABI-changing target, wchar, enum, and packing flags are rejected.

The versioned contract includes:

| Type or Surface | v1 Contract |
| --- | --- |
| Byte order / address space | little-endian / 64-bit |
| `char`, `short`, `int`, `long`, `long long` | 8, 16, 32, 64, 64 bits; plain `char` signed |
| `size_t`, `ptrdiff_t`, pointers | 64 bits |
| `wchar_t` | signed 32-bit code unit |
| `float`, `double` | IEEE binary32 / binary64 |
| `long double` | 80-bit extended precision in 16-byte storage, software CIL operations on every host |
| `__int128` | 128-bit integer storage and helpers |
| Varargs | fixed software SysV-style register/overflow representation, independent of host .NET calling convention |
| C++ objects | fixed Itanium layout and pinned libc++ ABI namespace/version |
| Fortran descriptors | Flang 22 CFI version 20240719 and fixed byte-stride layout |
| `FILE` | opaque runtime handle; never the host libc `FILE` layout |
| pthread objects | toolchain-owned sizes and managed synchronization state |
| errno | fixed numeric constants and invariant English error text |

The same portable CIL image is intended for little-endian 64-bit .NET hosts, including Windows/macOS/Linux and x64/ARM64. These cross-OS/architecture executions have not yet been validated here. Cross-platform conformance remains a required release gate, not an inferred result from IL-only metadata.

Runtime OS services are implemented through .NET APIs, which may themselves use the platform's C libraries. That implementation detail does not expose platform-specific ABI structures to compiled programs. Paths, filesystem capabilities, timezones, available resources, floating-point library last-bit details, and explicitly unsupported APIs can still vary; stable ABI does not mean every OS operation has identical availability.

The portable C library is a growing subset. Native C libraries or host-header bitcode cannot be injected with `--native-import` or `--system-library`. Recompile dependencies with the portable sysroot, or provide an ABI-matched managed interface. Unimplemented functions fail rather than falling back to native ABI calls. Cross-assembly CIL aggregate signatures are not yet a stable managed public API; use scalar/pointer exports with the documented C layout.

### Legacy Managed Host

`--runtime=managed-host` remains the default for compatibility with the original ffmpeg artifacts. It combines Linux host headers with managed libc adapters and is not portable. New deployments should explicitly choose `system` or `portable`; no existing artifact is silently reclassified as portable.

## Managed and Native Libraries

`-L/-l` continues to mean bitcode libraries in all modes. `--cil-reference` means an assembly with public `CExport` methods and matching ABI metadata. `--system-library` explicitly selects a native shared library for system-mode symbol resolution. The older `--native-import symbol=library!entrypoint` supports explicit scalar P/Invoke mappings in nonportable modes. These operations stay distinct to avoid accidental ABI mixing.

## Standard SPEC Configuration

[../config/spec/llvmnet.cfg](../config/spec/llvmnet.cfg) sets normal `CC`, `CXX`, and `FC` variables to the installed compiler aliases. Runtime mode is passed during compile and link; `--nativeaot` is passed only during link. No special runtime submit command is needed because the harness stages a normal native executable.

```sh
bash scripts/spec-harness.sh /path/installed-cpu2017 /path/toolchain system 505.mcf_r test
bash scripts/spec-harness.sh /path/installed-cpu2017 /path/toolchain portable 505.mcf_r test
```

The runner installs the configuration/flag description in the chosen harness. Its equivalent ordinary command, after sourcing the suite's environment, is:

```sh
runcpu --config=llvmnet --define llvmnet_dir=/path/toolchain \
  --define llvmnet_runtime=system --size=test --copies=1 --iterations=1 \
  --noreportable --tune=base 505.mcf_r
```

Both modes have passed normal CPU 2017 harness validation for C mcf, C++ deepsjeng, and Fortran exchange2 test workloads. A system-mode mcf one-copy reference workload also passed harness validation. The same configuration passed CPU 2026 system-mode zstd integer-rate and lbm FP-rate test workloads. These are successful benchmark validation runs, not full-suite reportable SPEC results. The configuration intentionally defaults to `reportable=0`; setting a flag to reportable does not satisfy SPEC rules. Full suites, prescribed iteration/run rules, complete flags/system disclosures, portability review, and validation are still required before publishing a valid SPEC score.

## Verification

`bash tests/toolchain-all.sh` covers compiler regressions, both ABI modes, real native C structs/varargs/data/callbacks, NativeAOT including native pthread TLS cleanup, portable C/C++/Fortran, compatible/incompatible managed links, relocation, and isolated-PATH deployment. It does not substitute for cross-platform CI or full SPEC validation.

The completed local run is recorded in `artifacts/toolchain-verification.log`. Post-change ffmpeg regression evidence is in `artifacts/ffmpeg-regression.log`; standard harness logs are `artifacts/spec-harness-*.log` and `artifacts/spec2026-harness-*.log`. Those logs and proprietary suite installations are intentionally not packaged with the compiler.