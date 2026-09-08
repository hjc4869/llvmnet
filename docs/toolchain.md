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

`-O3 -fvectorize -fslp-vectorize` explicitly opts into optimized vector IR. Before emitting executable or library CIL, LLVM expands reductions and scalarizes supported vector operations. Remaining fixed-vector lane values preserve LLVM's packed layout. This is vectorization compatibility with scalar execution, not a promise of hardware SIMD or support for every vector intrinsic. `--keep-ir` preserves the pre-legalization linked bitcode so vector generation can be inspected.

`--simd128` separately enables the owned wasm-compatible source-header subset backed by architecture-neutral .NET `Vector128<T>` helpers. It preserves vector memory operations and helper values while keeping the existing system/portable data ABI. The ffmpeg profile `FFMPEG_SIMD128=1` uses this for HEVC IDCT and 8-bit SAO; it does not retarget code to wasm32 or generally accelerate auto-vectorized LLVM IR. See [simd128.md](simd128.md).

Output selection is independent of ABI selection:

| Choice | Result |
| --- | --- |
| `-o program.dll` | CIL PE assembly, runtimeconfig, and runtime dependencies |
| `-o program` or no `-o` | Unix executable launcher plus `program.llvmnet/` payload; default name `a.out` |
| `-shared` / `--emit-library` | Managed CIL library, not a native shared object |
| `--nativeaot -o program` | RID-specific NativeAOT executable |
| `--emit-llvm -o program.bc` | Linked LLVM bitcode |
| `-r -o combined.o` | Relocatable linked bitcode |

NativeAOT options include `--runtime-id <rid>`, `--aot-optimize Balanced|Speed|Size`, `--aot-instruction-set baseline|native`, and `--aot-debug`. Instruction selection defaults to baseline; `native` uses the build host's CPU features and rejects a different RID. Such binaries may not execute on older CPUs even though the C data ABI is unchanged. The SIMD video measurements show a large baseline-AOT performance penalty that host-targeted publishing avoids; this is an explicit portability/performance choice, never a silent default. Invalid combinations such as `--nativeaot -c` or instruction selection without `--nativeaot` are errors. The current system ABI requires `linux-x64`. Other portable NativeAOT RIDs require an appropriate build host/toolchain and have not been executed in this environment.

## Runtime Modes

Choose the same mode for every source, object, archive, and managed reference in one program. LLVM module flags and managed assembly metadata carry the ABI identity. Incompatible combinations fail at link time, and incompatible loaded modules cannot share runtime state. Untagged legacy LLVM input is accepted only in `managed-host` mode.

### System ABI

```sh
llvmnet-clang --runtime=system -c source.c -o source.o
llvmnet-clang --runtime=system source.o -lm -pthread --nativeaot -o program
llvmnet-clang --runtime=system source.c --system-library /path/libexample.so -o program.dll
```

Identity: `system-linux-x64-v1`. This mode intentionally depends on the native Linux x86-64 SysV ABI and the selected host headers/libraries. C stdio, allocation, errno, math, file APIs, native data exports, and most pthread calls use real system libraries.

LLVM generates a small native ABI bridge for each imported call signature. CIL passes a byte argument record; LLVM emits the actual native call, including varargs, register-classified structs, `byval`, and `sret`. Registered native variadic pointers, such as `fcntl`, `open`, and `snprintf`, also use call-site-specific LLVM thunks. This avoids guessing complex native call shapes in P/Invoke signatures. Ordinary C callbacks use unmanaged-callable CIL thunks. Internal calls to translated callbacks resolve to managed targets, including legacy no-prototype C calls and aggregate-returning C++ virtual methods. Native invocation of translated variadic or aggregate callbacks is still explicitly rejected.

JIT output includes a generated native bridge shared library. NativeAOT embeds that bridge into the executable using static native inputs and direct imports, allowing SPEC to stage one executable. User-supplied dynamic libraries remain dynamic dependencies. Native globals resolve through the selected native library, not synthetic managed copies.

Native threads are real pthreads. A small adapter wraps thread start and TLS destructors to keep callbacks within .NET's thread lifetime, including NativeAOT. Cancellation, `pthread_exit`, hardware-fault signal recovery, and arbitrary foreign-thread TLS destructor interactions are not complete. C++ unwinding and Fortran-specific runtime calls still use managed compatibility adapters; this mode is not a complete native C++/Fortran runtime ABI replacement.

Defined LLVM TLS globals now use lazy per-thread initialized storage; external native TLS globals still require a bitcode definition. C++ thread-local destructors run before worker storage is released. Four-thread isolation, initialization, alignment, and reverse-order destruction are checked in both ABIs under JIT and NativeAOT. Direct `setjmp` calls and translated `longjmp` unwind/resume within managed frames, including dynamic allocation restoration. System `sigsetjmp` preserves requested signal masks; portable mask saving and jumps across native frames remain unsupported. LLVM stack-save/restore scopes use aligned owned allocations with cleanup, not native stack-pointer manipulation.

Empty or whole-line comment-only void inline assembly preserves ordering through a managed memory barrier. System mode additionally recognizes the exact x86 `int3` debugger trap and register-checked CPUID/XGETBV forms used by LLVM and Eigen host detection. CPU queries execute in LLVM-generated native thunks and are checked against native output; the trap delivers SIGTRAP. Other executable or value-producing assembly remains rejected, and portable mode rejects CPU-query assembly. This is not a general inline assembler.

`--trap-missing-arguments` is a system-only diagnostic option for invalid void calls to translated functions with fewer arguments than their definition requires. The default remains a compile error. Opting in emits a build diagnostic and an `InvalidProgramException` at the call site; entering that path fails rather than inventing values. WRF selects it for a legacy implicit-interface error outside the validated input path. Build success with this option is not proof that every program path is supported.

### Optional System C++ Library

[../scripts/build-system-cxx.sh](../scripts/build-system-cxx.sh) compiles selected, unmodified upstream libstdc++ 16 sources to system-ABI bitcode using matching host headers. It is an explicit optional archive, not a native C++ fallback or complete standard-library distribution. Source and build products stay under ignored artifacts. Obtain the matching upstream source separately, for example by downloading and extracting Debian `gcc-16-source=16.2.0-1`, then extracting its `libstdc++-v3`, `libiberty`, and `include` subtrees.

```sh
bash scripts/build-system-cxx.sh artifacts/vendor/gcc-16.2.0/libstdc++-v3
bash tests/system-cxx.sh artifacts/system-cxx/libstdcxx-system.a
bin/llvmnet --runtime=system program.cpp artifacts/system-cxx/libstdcxx-system.a -o program
```

The focused check compares file/string streams, numeric formatting, list/hash-table operations, shared/weak ownership, regexes, demangling, exceptions, futures and four synchronized C++ workers with native output at O0/O3 in JIT and NativeAOT. Futures include promise errors, timed waits, deferred work, cross-thread exceptions and completion at thread exit. Random-device configuration selects the upstream OS-entropy path instead of CPU-specific RNG instructions; seeded engines are compared exactly while entropy values are not. A C++17 filesystem fixture checks status, iteration, rename and removal. Keep source notices and the applicable GPL/GCC Runtime Library Exception with any redistribution; this archive is not automatically built or included by the package script. The portable libc++ profile remains separate and does not gain these host-header facilities.

### Optional System OpenMP

System-mode OpenMP uses LLVM's native C runtime with translated outlined functions entering through generated scalar callback thunks. It does not substitute native benchmark code. Stage the matching Debian headers/runtime without root privileges and run the focused four-worker check:

```sh
bash scripts/install-system-openmp.sh
bash tests/openmp.sh
bash tests/fortran-concurrent.sh
```

The default stage is `artifacts/system-openmp`, pinned to `1:22.1.8-1+b2`; package hashes and notices are retained. The fixtures test four workers, dynamic scheduling, reductions, barriers and Fortran concurrent loops at O0/O3 in JIT and NativeAOT. Flang 22 marks `-fdo-concurrent-to-openmp=host` experimental; this mode is enabled explicitly for the CPU 2026 `doconcurrent` set. Use `LLVMNET_OPENMP_PREFIX` for another staged prefix. Generated programs require the selected library on the runtime loader path; standalone users must provide `LD_LIBRARY_PATH` or a system installation. This dependency is not bundled automatically. Portable OpenMP, device offload and full OpenMP/TLS lifetime conformance are not implemented or claimed.

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

SPEC builds default to `-O3`, independently of the driver's scalar O1 default. The wrapper selects C++14 for CPU 2017 and C++17 for CPU 2026. Runtime, optimization, vectorization, C++ dialect, and thread count are included in build labels. Set `SPEC_OPT_LEVEL`, `SPEC_VECTORIZE=1`, or `SPEC_CXX_STANDARD` for explicit alternatives; set `SPEC_REBUILD=1` to force recompilation after a toolchain update. Rate uses one copy and one thread; speed defaults to one copy and four threads, with `SPEC_THREADS` available for explicit overrides. The wrapper passes the count to `runcpu` and OpenMP environment settings. For multi-thread runs the kit's OpenMP set also receives `-fopenmp -DSPEC_OPENMP`; system mode adds an explicit LLVM OpenMP library. `SPEC_OPENMP_PREFIX` selects staged headers/runtime (default `artifacts/system-openmp`), and `SPEC_OPENMP_LIBRARY` overrides the library. The staged directory is added to `LD_LIBRARY_PATH`. Thread count alone still does not prove worker activation; inspect source selection and build/run evidence. Kit-managed multiprocess speed entries use the same concurrency setting.

`SPEC_SYSTEM_CXX=/absolute/path/libstdcxx-system.a` adds the optional bitcode library to system C++ links only, and matrix environment records include its hash. Benchmark-specific portability settings are scoped to their suite version; they do not change licensed sources.

For a focused diagnostic retry only, `SPEC_REUSE_BUILD=1` requests the kit's `--make_no_clobber` mode, preserving existing makefiles and objects. Use it only when source flags and object ABI are unchanged, record the relinked toolchain/library, and still require harness execution and output validation. An already-built executable must be retained outside the build target path before retrying with a new runtime; otherwise make can report nothing to do and run the old image even with `SPEC_REBUILD=1`. Such builds are non-reportable. Matrix runs explicitly disable reuse and always rebuild.

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

Run every installed entry one at a time, system then portable, including speed variants that share sources:

```sh
bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain test
bash scripts/spec-matrix.sh artifacts/spec2026-harness artifacts/spec-toolchain test
SPEC_VECTORIZE=1 bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain test '505.*'
```

The matrix always rebuilds. It continues after failures and exits nonzero unless every case passes. Each run gets a unique directory under `artifacts/spec-matrix/` containing a TSV result for each benchmark/ABI, harness logs, snapshots of make logs, config/flags, and compiler/runtime hashes. Only a zero harness exit with the benchmark's explicit `Success:` validation marker is PASS. A build without validation is not PASS. `SPEC_TIMEOUT` bounds each build-plus-run (default 1800 seconds); TIMEOUT is incomplete evidence, not an unsupported-operation diagnosis. `SPEC_STACK_KB` sets the child's soft stack limit (default `unlimited`); the hard limit must permit it. Large Fortran automatic arrays can exceed an ordinary 8 MiB shell stack. `SPEC_MATRIX_OUTPUT` changes the output root. The optional fourth argument is a quoted shell pattern selecting benchmark names. Do not run two matrices concurrently against the same installed kit.

Before starting checks, the matrix re-executes a frozen copy of itself and uses copies of the harness wrapper and configuration from the same artifact directory. Later workspace edits cannot change an in-progress run. Keep the selected installed toolchain unchanged until completion. New runs write `completion.txt` only after every selected benchmark/ABI has a result row; its failure count may be nonzero. Absence of that record indicates an interrupted or incomplete run. The completed older inventories predate this marker and retain their terminal completion output.

`bash scripts/spec-summary.sh first/results.tsv retry/results.tsv` prints one row per suite/benchmark/input/optimization/vectorization/copy/thread/threading profile, with separate system/portable results. Historical nine-column tables are one-copy, one-thread profiles. Eleven-column multi-thread tables have unspecified activation; twelve-column rows distinguish `serial`, `kit-openmp-v1` (explicit OpenMP), and `kit-openmp-v2` (also host DO CONCURRENT lowering). These profiles do not overwrite each other. The profile identifies configuration, not whether an individual benchmark uses OpenMP, C++ threads, processes or serial code. Later files override earlier attempts for the same profile; missing ABI rows are explicitly marked MISSING. Supply only compatible C++ dialect/toolchain profiles when combining results. The label records the requested suite dialect; scoped overrides such as Parest's C++03 mode are recorded in config and make logs. Raw files retain compiler hashes and logs for provenance.

For repair-first progression, use [../scripts/spec-ordered.sh](../scripts/spec-ordered.sh):

```sh
SPEC_REQUIRED_ABIS=system SPEC_TIMEOUT=600 \
SPEC_SYSTEM_CXX="$PWD/artifacts/system-cxx/libstdcxx-system.a" \
  bash scripts/spec-ordered.sh artifacts/spec2017-harness artifacts/spec2026-harness artifacts/spec-ordered-toolchain test
```

It reads the kits' benchmark sets, sorts each numerically, and runs these groups in order: 2017 integer rate; 2026 integer rate; 2026 FP rate; 2026 integer speed; 2026 FP speed; 2017 FP rate; 2017 integer speed; 2017 FP speed. Each benchmark attempts system then portable sequentially. By default a validated system result gates advancement and portable failures remain visible; `SPEC_REQUIRED_ABIS=both` gates on both. It stops before the next benchmark on a failed required gate or an incomplete matrix. `SPEC_ORDER_START=2026-intrate:721.gcc_r` explicitly resumes at an entry without implying earlier coverage. The driver freezes scripts, config and benchmark sets, retaining progress and a COMPLETE/BLOCKED record under `artifacts/spec-ordered/`. Keep its toolchain and optional library unchanged while it runs.

Use `test`, `train`, or `ref` inputs explicitly. A test-input pass is not validation of train/reference inputs, parallel behavior, or a reportable score. Kits may add automatic OpenMP/thread suppression flags even when a thread count is configured. Inspect build/run evidence before claiming actual speed/parallel support. Proprietary sources, inputs, and raw diagnostic snapshots stay in ignored local artifacts, not tracked documentation.

## Verification

`bash tests/toolchain-all.sh` covers compiler regressions, both ABI modes, real native C structs/varargs/data/callbacks, NativeAOT including native pthread TLS cleanup, portable C/C++/Fortran, compatible/incompatible managed links, relocation, and isolated-PATH deployment. It does not substitute for cross-platform CI or full SPEC validation.

The completed local run is recorded in `artifacts/toolchain-verification.log`. Post-change ffmpeg regression evidence is in `artifacts/ffmpeg-regression.log`; standard harness logs are `artifacts/spec-harness-*.log` and `artifacts/spec2026-harness-*.log`. Those logs and proprietary suite installations are intentionally not packaged with the compiler.