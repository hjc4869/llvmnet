# Changelog

All notable user-visible changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the compiler follows [Semantic Versioning](https://semver.org/) as described in [docs/dev/versioning.md](docs/dev/versioning.md). Each entry names the plan item it implements, such as `FD-05`.

## [Unreleased]

### Added

- MIT license, third-party notices, contribution guide and this changelog (FD-01 to FD-04).
- `--version` reports the build and LLVM versions; emitted assemblies record them and refuse an incompatible runtime (FD-05).
- Pinned upstream sources through `scripts/fetch-sources.sh` (FD-06).
- Parallel test runner `tests/run.sh`, unit tests, JIT validation of emitted assemblies, CI workflow and dev container (FD-11 to FD-15, FD-19).
- Source-located diagnostics, kept IR on failure and exit codes 1/2/3 (BE-04).
- `--diagnose-limits` (BE-25) and `--check-indirect-calls` (BE-18).
- 128-bit atomics; 65- to 128-bit integer intrinsics; constrained floating-point intrinsics; masked memory intrinsics; `llvm.objectsize`, `exp10`, `canonicalize`, `minimumnum`/`maximumnum`, saturating shifts.
- Global aliases, undefined weak symbols and alias exports from managed libraries (BE-07).
- Differential fuzzing test `tests/fuzz.sh` with pinned csmith and YARPGen, run nightly (FD-16).
- The portable sysroot `sysroot/dotnet64-v2`: musl 1.2.5, libc++/libc++abi and flang-rt 22.1.8 compiled to LLVM bitcode by `scripts/build-sysroot.sh`, which `ensure-built.sh` runs (LC-15, CX-07, FT-05), and compiler-rt's complex arithmetic builtins (LC-29).
- A host layer with Linux system-call semantics: pipes, `poll`/`select`, symbolic links, shared file mappings, `posix_spawn`/`system`/`popen`, signal masks and signals from outside, resource limits, robust mutexes, thread cancellation; contract in `docs/dev/host-abi.md` (LC-04 to LC-12, LC-28).
- `-nostdlib`, `-nodefaultlibs`, `-nostartfiles`, `-fsyntax-only`; `-x ir -c` accepts `.ll` and `.bc` inputs; `--portable-sysroot` (LC-16).
- Conformance runs: `tests/libc-conformance.sh` (musl libc-test), `tests/libcxx-conformance.sh` (libc++ lit suite), `tests/fortran-conformance.sh` (FCVS and gfortran), nightly in CI (LC-19, CX-08, FT-06).
- `abort` prints the C call stack; `LLVMNET_CHECK_DESCRIPTORS=1` reports descriptors open at exit.
- Time zones on hosts without zoneinfo: the host layer serves TZif images generated from `TimeZoneInfo`; `LLVMNET_ZONEINFO=managed|host` selects the source (LC-09).
- OpenMP in portable mode: LLVM's OpenMP runtime compiled into the sysroot with `ports/openmp`, so `-fopenmp` works for C, C++ and Fortran, including Flang's `DO CONCURRENT` host lowering; the default team size is the processor count .NET reports (FT-08).
- Aggregates through variadic calls follow the System V classification, in registers or the overflow area (BE-19).
- The `long double` rounding and transcendental intrinsics (`llvm.log.f80` and the others) become calls to the C library's `long double` functions (BE-37).
- Portable threads get the stack size `pthread_attr_setstacksize` asks for (at least 8 MB), and `getrlimit(RLIMIT_STACK)` reports the process's limit on Linux, so OpenMP worker stacks follow `OMP_STACKSIZE` and the stack limit as with native libomp.
- Portable images run on Windows x64, macOS ARM64 and Linux ARM64 under the JIT and as NativeAOT executables, checked by the `tests/xp/` bundle and CI jobs on those runners (XP-01 to XP-03). On Windows the host layer presents drives as `/c/...`, maps `/tmp` to the temporary directory, creates directory links, maps Win32 errors to errno values, runs `sh.exe` from Git for Windows for `/bin/sh` and switches an attached console to UTF-8; `docs/dev/host-abi.md` lists what differs.
- `--aot-project <dir>` writes a relocatable NativeAOT project for publishing on another host, and `--nativeaot --runtime-id linux-arm64` cross-publishes from Linux x64 with Debian's cross packages (XP-03, XP-05).
- An unsupported host (32-bit, big-endian, Mono, .NET before 10) prints one `llvmnet:` line naming the problem and exits with status 1 before any translated code runs, and `runtimeconfig.json` pins the .NET 10 band with `rollForward: Minor` (XP-06).
- `getrandom`, `/dev/urandom` and the start vector's `AT_RANDOM` read the host's `/dev/urandom` on Unix hosts, so a portable program no longer needs OpenSSL on Linux (XP-07).
- Link inputs (TC-02): `-c` on `.ll`, `.bc` and `.o` inputs, `-l:<file>`, `-Wl,--whole-archive`, archives in any order with rescanning, duplicate definitions reported with both files (`duplicate definition of 'f' in b.o; it is already defined in a.o`), `-r` of archives, and the `-nostdlib`, `-nodefaultlibs`, `-nostartfiles` and `-nostdlib++` family, documented in `docs/user/driver.md`.
- Every optimization level is tested against native output in both modes, and `-O0` and `-O3` images are JIT-validated (TC-03); `-fno-exceptions`, `-fno-rtti`, `-fvisibility`, `-g` and the per-alias language defaults are tested (TC-04).
- llvmnet's own errors and notes use Clang's colors on a terminal or with `-fcolor-diagnostics`, and `-fno-color-diagnostics` or `-fdiagnostics-color=` turns them off as for the frontend (TC-05).
- The documentation is `docs/user/` (getting started, driver, runtime modes, linking, deployment, limitations, troubleshooting, support matrix, SIMD128), `docs/dev/` (architecture, host ABI, calling convention, C++ bridge, versioning, testing, adding a host function, release process, artifacts, SPEC, ffmpeg, video benchmark) and `docs/evidence/`; `README.md` is written for users (TC-16). `docs/toolchain.md`, `docs/status.md` and `docs/roadmap.md` are gone.
- `docs/user/limitations.md`, generated from the support matrix and the runtime's table of refused system calls (`llvmnet --print-host-limitations`); the sockets, `epoll`, `inotify`, System V IPC, extended-attribute and other calls .NET cannot provide are now refused by name with a documented errno (TC-17).
- Release artifacts (TC-11): `scripts/release-artifacts.sh` builds the thin and bundled-LLVM `tar.xz` packages, the `llvmnet` dotnet tool package and `SHA256SUMS` reproducibly, and the `release` workflow builds, tests, signs (Sigstore, keyless) and publishes them from a `v*` tag.
- NativeAOT provisioning (TC-12): `scripts/nativeaot-feed.sh` fills an offline NuGet folder feed, `LLVMNET_NUGET_FEED=<dir>` publishes from it alone, and a missing prerequisite (the .NET 10 SDK, a C compiler, zlib, the aarch64 cross sysroot, a feed package, or a target that another host must publish) is reported in one line naming the fix before `dotnet publish` runs.
- Table-driven driver options (TC-01): every option is handled, passed to the frontend, accepted without effect or rejected with a reason, per runtime mode; `--help` and the tables of `docs/user/driver.md` are generated from the table, and `tests/driver-options.sh` feeds every option Clang's and Flang's `--help` list to the driver. New: `-###` (print the frontend commands), `--driver-mode=`, `-x` per input, `-l:<file>`, `-Wl,--whole-archive`, `-Xlinker`, `-nostdlib++`, `-c` on `.o` inputs, and `-mllvm` in portable mode.

- Benchmark harness `scripts/benchmark.py` (PF-01): 58 benchmarks (`tests/perf/benchmarks.tsv`: microbenchmarks, free `llvm-test-suite` programs, the ffmpeg decode harness at one and four threads) built natively, for the JIT and as NativeAOT from the same sources, outputs checked against native, medians compared with `tests/perf/baseline.tsv`; the nightly CI job fails on a confirmed regression. Method in `docs/dev/benchmarks.md`.
- `--aot-instruction-set x86-64-v3` (x64 targets) compiles NativeAOT executables for AVX2-class CPUs; with it the SIMD128 ffmpeg profile's shuffles use `pshufb` instead of a software fallback (PF-08). Baseline stays the default.
- The CMake toolchain file states the compiler identity CMake cannot read from bitcode objects and launchers (Clang of the bundled LLVM version, 8-byte little-endian pointers, `gnu17`/`gnu++17` defaults), so `CMAKE_SIZEOF_VOID_P`, compile features and `C_STANDARD` work; it is installed as `share/llvmnet/cmake/llvmnet.cmake`, as the user documentation said, with the Meson cross file as `share/llvmnet/meson/llvmnet.ini`, and finds the package's own drivers and archive tools. The checkout's `bin/` has the `llvmnet-clang`, `llvmnet-clang++` and `llvmnet-flang` aliases the package has (TC-07, found by WG-01).
- A link input whose extension names no source language is linked by its content, as Clang hands such a file to the linker: an LLVM bitcode object or archive under any name, such as CMake's `.obj` (found by WG-01).
- In portable mode the frontend queries answer for the portable target (`-dumpmachine` prints `x86_64-unknown-none`), `-print-libgcc-file-name` is answered, and `--target=x86_64-unknown-none` names the portable target without changing anything, so build systems that pass back what `-dumpmachine` reported configure (found by WG-01).
- Empty `asm` statements whose one register output is tied to an input (`asm("" : "+r"(x))`, a common optimization barrier) yield that input in both modes; 12 gcc-c-torture tests use them (found by WG-02).
- `llvm.vector.reduce.fmax`, `fmin`, `fmaximum` and `fminimum` without `nnan`, which a loop vectorized by `#pragma clang loop vectorize(enable)` produces, are folded lane by lane (BE-43).
- A K&R-style `main(argc)` gets the argument count, as natively (found by WG-02).
- `tests/llvm-test-suite.sh` builds llvm-test-suite's SingleSource and MultiSource, gcc-c-torture included, through the suite's own CMake and lit in portable, system and portable NativeAOT modes against native builds; `tests/conformance/llvm-test-suite.tsv` classifies every known failure, and an unclassified one fails the run (WG-01, WG-02).

### Changed

- **Breaking:** `--runtime=portable` is the default, and the portable ABI tag is `dotnet64-v2`, the musl layout with glibc-width fast integer types; `dotnet64-v1` objects are rejected (LC-26, ADR-09).
- **Breaking:** `--runtime=managed-host` is removed, and link inputs without an ABI tag are rejected (ADR-04).
- System mode links flang-rt built against glibc instead of the C# Fortran runtime (FT-09).
- Portable C++ takes `type_info`, `dynamic_cast` and catch matching from libc++abi (CX-03); main-thread `thread_local` destructors run before `atexit` handlers as in glibc (CX-12).

- Identical inputs now produce byte-identical assemblies (BE-05).
- Atomics are lock-free on naturally aligned locations, about five times faster under contention (BE-16).
- Aligned 32- and 64-bit atomics are emitted inline as `Volatile.Read` and `Interlocked` operations instead of runtime calls, and portable futexes keep waiters in hashed buckets with exact wake counts and real `FUTEX_REQUEUE`: contended mutexes 1.7 times and atomics 1.6 times faster (PF-02).
- Taking a function's address no longer registers it with the runtime each time; the module initializer registers every address-taken function once (PF-13).
- Portable streams have 4,096-byte buffers, the size glibc uses on Linux file systems, instead of musl's 1,024, so buffered output is flushed where native builds flush it; `BUFSIZ` stays 1,024. `long double` comparisons no longer go through `BigInteger` (PF-03).
- Translated functions that keep stack buffers (allocas, `byval` copies, variadic call areas) no longer zero them on entry, and allocas in functions with variable-length arrays or `setjmp` come from a per-thread arena: H.264 decoding about 30% faster, variadic calls and printf about a quarter. Programs that read uninitialized stack memory now see whatever was there before, as natively (PF-04, BE-17).
- 32- and 64-bit rotates and funnel shifts compile to `BitOperations.RotateLeft`/`RotateRight` or width-specific helpers instead of a width-generic helper (salsa20 as NativeAOT about 8 times faster), and emitted images turn off the JIT's dynamic PGO, about 7% faster on average; `DOTNET_TieredPGO=1` restores it (PF-06).
- Fewer methods are JIT-compiled before `main` (134 instead of 194 for a hello world), and refused system calls are looked up in an array: a JIT hello world starts in 36 ms instead of 39 (PF-10).
- `long double` arithmetic in portable mode (`Float80`) computes in 64- and 128-bit integers instead of `BigInteger` values, with the same results bit for bit: the Float80 microbenchmark is 4.9 times faster under the JIT and 8.4 times as NativeAOT, and printf, whose `%f` formatting computes in `long double`, 2.4 and 2.3 times (PF-14).
- `atexit` and C++ static destructors run interleaved in reverse registration order, destructor attributes last (BE-08).
- Portable mode accepts the x86-64 register-slot equivalences that system mode accepts for direct calls: `ptr` and `i64`, `i32` and `i64` in register positions, `i1` and `i8`; the entry point accepts Flang's `i64 main` under `-fdefault-integer-8`, and `--trap-missing-arguments` is available (BE-39). Both modes convert `i8` and `[1 x i8]` at direct calls, as Fortran passes `character(1)` to C `char` (BE-40).
- Portable `memcpy`, `memmove` and `memset` use the runtime's vectorized copy and fill (LC-24).
- In portable mode `_GNU_SOURCE` also declares the LFS64 names (`stat64`, `off64_t`, `open64`), as glibc does.
- The bundled toolchain package takes only the builtin headers from Clang's resource directory (not its sanitizer and profiling runtimes) and strips the copied binaries: 609 MB to 533 MB installed (TC-13).
- A managed library (`-shared`) exports only its external, default-visibility definitions, as a native shared library does; static functions and hidden-visibility definitions are private methods (TC-04). `-r` no longer links the runtime libraries into a relocatable object.
- Native-only options that build systems pass (`-fPIC`, `-pie`, `-Wl,-rpath`, `-Wl,--as-needed`, `-Wl,-z,...`, `-s`, `-static-libgcc`, `-rtlib=`, `-z`) are accepted without effect instead of rejected; `-v` prints the reason. `-m<option>` in portable mode, native linker options without a CIL equivalent (`-e`, `-T`, `-u`, `-Wa,`, `-Wl,--wrap`, ...), `-fsanitize=` and profiling instrumentation now fail as `option '...' is not accepted: <reason>` (TC-01).
- `tests/fuzz.sh` runs every generated program with a resident memory limit, `FUZZ_MEMORY_MB` (2,048 MB by default; the llvmnet builds get 512 MB more for the .NET runtime), and with at most four jobs by default: YARPGen programs larger than the limit are skipped with the reason instead of exhausting the host's memory, and an llvmnet build over its limit fails like a hang (FD-16).
- `tests/ffmpeg-threads.sh` publishes and checks both NativeAOT decoders by default (`FFMPEG_THREADS_AOT=0` checks the JIT only), so the nightly job covers the threaded and SIMD128 ffmpeg profiles in both ABIs under the JIT and NativeAOT (WG-05).

### Removed

- The hand-written C library, the C# Fortran runtime, the hand-written portable headers and the MathNet.Numerics dependency (LC-17).
- **Breaking:** `--native-import symbol=library!entrypoint`, the explicit scalar P/Invoke path of system mode; the option fails with guidance to use `--system-library`, whose generated bridges cover every signature it handled (SA-01, ADR-04).

### Fixed

- System mode: a call through a K&R or other contradicting declaration of a native function whose result type differs (`extern int free();` calling `void free(void *)`) produced IL the JIT rejected (`InvalidProgramException`). The call site's result is now reconciled with the native function's as for translated functions: an unused value is dropped, an ignored result popped, pointers and 64-bit integers converted, anything else rejected at compile time (found by llvm-test-suite's cfrac, WG-01).
- System mode: `rand()` before any `srand()` gave a different sequence on every run, because the .NET runtime calls `srandom(time)` while it starts and the program shares the C library with it. The runtime seeds with 1 again before `main`, as C requires (found by llvm-test-suite's ft, WG-01).
- Portable mode: `EXECUTE_COMMAND_LINE` reported `CMDSTAT` 7 ("Killed by signal") after every successful command: flang-rt passed `system()`'s status to musl's `WIFSIGNALED` as a 64-bit integer, for which the macro is true when the status is 0. `ports/flang-rt/execute-status.patch` passes it as an `int` (found by flang-rt's unit tests, WG-03).
- Portable `open` with `O_CREAT` and read-only access (`O_RDONLY | O_CREAT | O_EXCL`) failed with `EINVAL`; it creates the file whatever its mode bits, and the descriptor still only reads (found by flang-rt's unit tests, WG-03).
- Out-of-range `long double` to integer conversions threw instead of producing the x87 integer-indefinite value.
- Zero-size `alloca` returned a null pointer, which flang-rt read as an unallocated zero-length character variable.
- Signaling NaN `float` constants were quieted by the .NET JIT at `-O0`.
- A `catch (...)` after a typed handler could enter the typed handler: both received selector 1 when that clause held the first type id.
- An exception no handler catches now calls `std::terminate` and the installed handler instead of ending as an unhandled managed exception.
- System-mode pointer handlers received the address of the thrown pointer instead of its value, and did not accept added `const` or base-class pointers.
- Programs with more than about 65,000 globals, constants and data blocks failed to load ("too many fields").
- Portable paths with a trailing slash (`mkdir("dir/")`) or repeated slashes failed with `ENOENT`.
- `std::filesystem::copy_file` failed for every file in portable mode: the host now implements `sendfile` and `copy_file_range`.
- Timed futex waits and `nanosleep` could end up to a millisecond early, so `condition_variable::wait_for` reported no timeout and callers waiting for one never returned.
- NativeAOT executables for Windows resolved IANA time-zone names (`TZ=Europe/London`) to UTC, because the generated project disabled ICU for every target; it now does so only for Linux and macOS (PF-10).
- After PF-04, long-running loops in functions called once (lpbench's `dgefa`) ran up to 2.6 times slower under the JIT, because its on-stack replacement code is worse without `localsinit`; only functions that use `localloc` drop the flag now.
- Portable robust priority-inheritance mutexes whose owner died, and `pthread_cancel` during a futex wake, could leave waiters blocked after the PF-02 futex change; fixed before release (`functional/pthread_robust`, `functional/pthread_cancel-points` and `regression/pthread_cancel-sem_wait` pass again).
- On Linux and macOS, `lseek` on a portable program's standard input redirected from a file failed with `ESPIPE` and `fstat` reported a FIFO, so `rewind(stdin)` silently did nothing (mafft's `pairlocalalign` read no sequences). A standard stream redirected from or to a regular file now seeks and stats as that file, and reads still move the offset the shell shares.
- Portable `long double`: dividing by or taking a remainder by a pseudo-zero (a zero significand with a nonzero exponent) threw `DivideByZeroException`, infinity times one gave infinity instead of NaN, and converting one with a large exponent to `double` gave infinity; it is a zero in every operation now, as it already was in comparisons. Converting an out-of-range `long double`, an infinity or a NaN to `__int128` threw `OverflowException`; such values saturate now, as with compiler-rt's `__fixxfti` natively (PF-14).

- System-mode `exit()` skipped `atexit` handlers and all destructors.
- `long double` stores overwrote the 6 bytes after the value.
- Wide float-to-integer conversions threw for legal and out-of-range inputs.
- `lround`/`llrint`/`is.fpclass` on `long double` produced invalid IL.
- Output names such as `System.dll` collided with framework assemblies at run time; they are now rejected.
- `scripts/build-sysroot.sh` compiled only the ports' own musl sources where `awk` is mawk (Debian's default, so the dev container and CI), leaving the portable libc without `stdout`, `environ` or `atexit`; the source selection no longer depends on awk's evaluation order.

## [0.2.0] - 2026-09-08

Prototype baseline: C, C++ and Fortran through Clang/Flang 22 to CIL; `managed-host`, `system` and `portable` runtime modes; NativeAOT output; relocatable toolchain package; threaded and SIMD128 ffmpeg profile; SPEC CPU 2017/2026 test-input coverage in system mode. See the status page of that release (now `docs/user/limitations.md` and the evidence files).
