# SPEC CPU

llvmnet is checked against the SPEC CPU 2017 and CPU 2026 suites in both runtime modes, through the standard `runcpu` harness. The suites are licensed and are not part of the repository; install each kit under `artifacts/` (the examples use `artifacts/spec2017-harness` and `artifacts/spec2026-harness`), and never commit kit material or run outputs. This page covers the configuration, the scripts, the per-benchmark settings, and the portable-mode exclusions; results are recorded under [evidence/](../evidence/).


## What a check is

- The benchmark builds with [config/spec/llvmnet.cfg](../../config/spec/llvmnet.cfg): `llvmnet-clang`, `llvmnet-clang++` and `llvmnet-flang` from a packaged toolchain, `-O3`, C++14 for CPU 2017 and C++17 for CPU 2026, linked with NativeAOT.
- It runs one copy. Rate benchmarks use one thread and speed benchmarks four. With more than one thread, the kits' OpenMP benchmarks build with `-fopenmp -DSPEC_OPENMP`, and CPU 2026 Fortran `DO CONCURRENT` loops are lowered to OpenMP (`-fdo-concurrent-to-openmp=host`). System mode links the LLVM OpenMP runtime `libomp.so.5`, by default the one `scripts/install-system-openmp.sh` stages in `artifacts/system-openmp`; portable mode has no OpenMP runtime.
- `PASS` means runcpu exited successfully and validated the benchmark's output. `FAIL` covers build errors, run errors and output mismatches. `TIMEOUT` means the build and run did not finish within `SPEC_TIMEOUT`, which says nothing about the cause.
- Runs are non-reportable (`reportable = 0`) and use the input size given (`test`, `train` or `ref`). They are compatibility checks, not SPEC scores.

`SPEC_VECTORIZE=1` adds `-fvectorize -fslp-vectorize`. The backend scalarizes the resulting vector IR, so this checks vectorized IR, not SIMD code generation.

## Scripts

| Script | Purpose |
| --- | --- |
| `scripts/spec-harness.sh suite toolchain [runtime] [benchmark] [size]` | One benchmark in one mode. |
| `scripts/spec-matrix.sh suite toolchain [size] [glob]` | Every installed entry of one kit, system then portable, into a new directory under `artifacts/spec-matrix/`. |
| `scripts/spec-ordered.sh cpu2017 cpu2026 toolchain [size]` | All eight benchmark groups in a fixed order, into `artifacts/spec-ordered/`. It stops before the next benchmark when a required mode fails: system by default, both with `SPEC_REQUIRED_ABIS=both`. `SPEC_ORDER_START=group:benchmark` resumes at an entry. |
| `scripts/spec-portable-check.sh baseline toolchain [glob]` | Reruns the entries of a baseline that did not pass, in portable mode; see [reproducing the check](#reproducing-the-check). |
| `scripts/spec-portable-audit.mjs directory` | The latest portable result per benchmark, with the first diagnostic of each failure. |
| `scripts/spec-summary.sh results.tsv...` | Combines result tables, oldest first, into one row per benchmark and profile with the system and portable results; a missing mode shows as `MISSING`. |

Each run directory holds frozen copies of the scripts and configuration, the toolchain hashes, `results.tsv`, harness logs, copies of the make logs and, once every selected entry has a result, `completion.txt`. A directory without `completion.txt` is an interrupted run. Keep the toolchain unchanged while a run uses it, and do not run two checks against the same installed kit at once. Environment variables such as `SPEC_TIMEOUT`, `SPEC_THREADS`, `SPEC_CXX_STANDARD`, `SPEC_SYSTEM_CXX` and `SPEC_OPENMP_PREFIX` are described in [the configuration section](#standard-spec-configuration).

`scripts/spec-harness.sh`, and therefore every script above, stops unless `artifacts/ffmpeg-browser/video-validation/native-frames.txt` and `cil-frames.txt` exist and are equal.

```sh
bash scripts/package-toolchain.sh artifacts/spec-toolchain
bash scripts/spec-harness.sh artifacts/spec2017-harness artifacts/spec-toolchain portable 505.mcf_r test
SPEC_TIMEOUT=1800 bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain test
SPEC_TIMEOUT=1800 bash scripts/spec-matrix.sh artifacts/spec2026-harness artifacts/spec-toolchain test
SPEC_REQUIRED_ABIS=both bash scripts/spec-ordered.sh artifacts/spec2017-harness artifacts/spec2026-harness artifacts/spec-toolchain test
bash scripts/spec-summary.sh first/results.tsv retry/results.tsv
```

## Benchmark settings

The configuration applies these settings in both modes. The settings for portable mode only are in [the portable settings](#portable-settings-in-the-spec-configuration).

| Benchmarks | Settings |
| --- | --- |
| 500.perlbench_r, 600.perlbench_s | `-DSPEC_LINUX_X64` |
| 502.gcc_r, 602.gcc_s | `-fgnu89-inline` |
| 510.parest_r | `-std=gnu++03` |
| 521.wrf_r, 621.wrf_s | `-DSPEC_CASE_FLAG`, `-fconvert=big-endian`, `--trap-missing-arguments` |
| 523.xalancbmk_r, 623.xalancbmk_s | `-DSPEC_LINUX`, `-fdelayed-template-parsing` |
| 525.x264_r, 625.x264_s | `-fcommon` |
| 526.blender_r | `-funsigned-char` |
| 527.cam4_r, 627.cam4_s | `-std=gnu89 -DSPEC_CASE_FLAG`, `-Inetcdf` |
| 628.pop2_s | `-DSPEC_CASE_FLAG`, `-Inetcdf -fconvert=big-endian` |
| 735.gem5_r, 835.gem5_s | `-Ulinux` |

WRF's bundled NetCDF uses `SPEC_CASE_FLAG` for lowercase wrapper names, and its radiation tables are big-endian unformatted files. One WRF source call omits a required argument: `--trap-missing-arguments` turns that call into a run-time trap ([dev/calling-convention.md](calling-convention.md#direct-calls)), which the test input does not reach.

Speed benchmarks do not all run OpenMP workers. 835.gem5_s is single-threaded (its kit metadata adds `-DSPEC_AUTO_SUPPRESS_THREADING`), and 827.cppcheck_s runs its checks in child processes, passing `-j` with the thread count.

## Standard SPEC Configuration

[../../config/spec/llvmnet.cfg](../../config/spec/llvmnet.cfg) sets the ordinary `CC`, `CXX` and `FC` variables to the installed compiler aliases. The runtime mode is passed at compile and link time and `--nativeaot` at link time, so the harness stages an ordinary native executable.

SPEC builds default to `-O3`, C++14 for CPU 2017 and C++17 for CPU 2026. Build labels include the runtime, optimization, vectorization, C++ dialect and thread count. Environment overrides:

| Variable | Effect |
| --- | --- |
| `SPEC_OPT_LEVEL`, `SPEC_VECTORIZE=1`, `SPEC_CXX_STANDARD` | optimization level, vectorization, C++ dialect |
| `SPEC_REBUILD=1` | force recompilation, for example after a toolchain update |
| `SPEC_THREADS` | thread count; rate entries default to 1, speed entries to 4. Above 1, the kit's OpenMP set gets `-fopenmp -DSPEC_OPENMP` and CPU 2026's DO CONCURRENT set host lowering |
| `SPEC_OPENMP_PREFIX`, `SPEC_OPENMP_LIBRARY` | system mode with more than one thread: staged OpenMP headers and runtime (default `artifacts/system-openmp`), linked with `--system-library` and added to `LD_LIBRARY_PATH` |
| `SPEC_SYSTEM_CXX=/abs/path/libstdcxx-system.a` | add the optional libstdc++ archive to system-mode C++ links |
| `SPEC_STACK_KB` | soft stack limit of the run (default `unlimited`; large Fortran automatic arrays exceed 8 MiB) |
| `SPEC_REUSE_BUILD=1` | the kit's `--make_no_clobber`, for diagnostic retries with unchanged flags and object ABI; matrix runs never reuse builds |

```sh
bash scripts/spec-harness.sh /path/installed-cpu2017 /path/toolchain system 505.mcf_r test
bash scripts/spec-harness.sh /path/installed-cpu2017 /path/toolchain portable 505.mcf_r test
```

The runner installs the configuration and flags description in the harness. The equivalent command, after sourcing the suite's environment, is:

```sh
runcpu --config=llvmnet --define llvmnet_dir=/path/toolchain \
  --define llvmnet_runtime=system --size=test --copies=1 --iterations=1 \
  --noreportable --tune=base 505.mcf_r
```

The configuration sets `reportable=0`. A reportable result needs full suites, SPEC's run rules and complete flag and system disclosures.

### Matrix, summary and ordered runs

```sh
bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain test
bash scripts/spec-matrix.sh artifacts/spec2026-harness artifacts/spec-toolchain test
SPEC_VECTORIZE=1 bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain test '505.*'
```

`scripts/spec-matrix.sh` runs every installed entry one at a time, system then portable, always rebuilding. It continues after failures and exits nonzero unless every case passes. A case passes only when the harness exits with 0 and prints the benchmark's `Success:` validation marker. Each run gets a directory under `artifacts/spec-matrix/` (`SPEC_MATRIX_OUTPUT`) with a TSV row per benchmark and mode, harness and make logs, the configuration and compiler and runtime hashes, and a `completion.txt` written once every row exists. `SPEC_TIMEOUT` bounds each build and run (default 1800 seconds). The optional fourth argument is a shell pattern of benchmark names. The script runs a frozen copy of itself, the harness wrapper and the configuration, so later workspace edits do not affect a running matrix; keep the installed toolchain unchanged until it finishes, and do not run two matrices against the same installed kit.

`bash scripts/spec-summary.sh first/results.tsv retry/results.tsv` prints one row per suite, benchmark, input, optimization, vectorization, copies, threads and threading profile, with separate system and portable results; later files override earlier ones and missing modes show as MISSING.

[../../scripts/spec-ordered.sh](../../scripts/spec-ordered.sh) runs the matrix one benchmark at a time in this group order: 2017 integer rate, 2026 integer rate, 2026 FP rate, 2026 integer speed, 2026 FP speed, 2017 FP rate, 2017 integer speed, 2017 FP speed. It stops at the first benchmark whose required modes fail (`SPEC_REQUIRED_ABIS`, `system` by default or `both`) and records COMPLETE or BLOCKED under `artifacts/spec-ordered/`. `SPEC_ORDER_START=2026-intrate:721.gcc_r` resumes at an entry.

```sh
SPEC_REQUIRED_ABIS=system SPEC_TIMEOUT=600 \
SPEC_SYSTEM_CXX="$PWD/artifacts/system-cxx/libstdcxx-system.a" \
  bash scripts/spec-ordered.sh artifacts/spec2017-harness artifacts/spec2026-harness artifacts/spec-ordered-toolchain test
```

`scripts/spec-portable-check.sh` reruns portable-mode entries against a baseline result file; [Portable mode](#portable-mode) describes it and the causes of the remaining portable failures. Choose `test`, `train` or `ref` inputs explicitly. Licensed sources, inputs and run outputs stay under the ignored `artifacts/` directory.


## Portable mode

In portable mode (`--runtime=portable`, ABI `dotnet64-v2`), SPEC CPU 2017 and CPU 2026 benchmarks compile against musl, libc++/libc++abi, LLVM's OpenMP runtime and flang-rt from the portable sysroot. This page lists the benchmarks that do not pass in portable mode and why, the settings the SPEC configuration applies for portable mode, and how to rerun the check. A check builds the benchmark at `-O3` with NativeAOT and runs it with test inputs; it passes when runcpu validates the output. the sections above describes the SPEC tooling in general.

| Cause | Benchmarks |
| --- | --- |
| [glibc behavior that musl does not share](#glibc-behavior) | 507.cactuBSSN_r, 607.cactuBSSN_s, 737.gmsh_r, 748.flightdm_r |
| [`fork`](#fork) | 500.perlbench_r, 600.perlbench_s, 827.cppcheck_s |
| [Prototypes that contradict the definition](#prototypes-that-contradict-the-definition) | 722.palm_r, 822.palm_s, 621.wrf_s |

All other CPU 2017 and CPU 2026 entries pass in portable mode, some with the [portable settings](#portable-settings-in-the-spec-configuration) below. For portable parity with system mode ([ADR-14](../plan/decisions.md#adr-14-linux-identity-of-the-portable-target)), every entry in the table is a listed exclusion: programs that need `fork`, programs that rely on glibc behavior musl does not share and fail natively against musl too, and prototypes that contradict the definition; each needs a source change the SPEC rules do not allow.

### OpenMP

Portable mode links LLVM's OpenMP runtime from the sysroot (FT-08, `ports/openmp`), so with more than one thread the SPEC configuration builds the kits' OpenMP benchmarks with `-fopenmp -DSPEC_OPENMP` and lowers CPU 2026 Fortran `DO CONCURRENT` loops to OpenMP (`-fdo-concurrent-to-openmp=host`) in both modes; `--system-library` is only for the native runtime in system mode. The speed benchmarks that need OpenMP run on four workers over musl's pthreads; those that use C++ threads or pthreads instead run the same way. The entries that do not pass are listed above by cause.

### Linux platform detection

Portable code sees neither `__linux__` nor `__unix__` ([ADR-14](../plan/decisions.md#adr-14-linux-identity-of-the-portable-target)). Programs that choose their platform code by those macros build with them as a per-benchmark setting, which makes the program, not llvmnet, responsible for the Linux interfaces it then uses. With the settings in the table below, all of these pass:

- **523.xalancbmk_r, 623.xalancbmk_s:** Xerces detects its platform through `__linux__`, and its Linux platform file, selected by `-DSPEC_LINUX`, includes the kernel header `linux/limits.h` for `PATH_MAX`. The portable sysroot has no kernel headers; `config/spec/include/linux/limits.h` includes `<limits.h>` instead.
- **723.llvm_r, 823.llvm_s:** `SwapByteOrder.h` includes the BSD header `machine/endian.h` when it does not detect Linux. The benchmark's prebuilt `config.h` also includes glibc's `execinfo.h`, whose functions it calls only with backtraces enabled, which they are not; `config/spec/include/execinfo.h` declares them.
- **735.gem5_r, 835.gem5_s:** gem5's libelf defines `roundup2` and its byte order only for the systems it recognizes, such as glibc and Linux. gem5 also uses `mode_t` where only glibc's headers have declared it.
- **772.marian_r, 872.marian_s:** marian's path library stops with `#error Unsupported system.` without `__unix__`, and finds the executable's path through `/proc/self/exe` only under `__linux__`.

### glibc behavior

These programs rely on glibc behavior that no flag selects; built natively against musl, they fail the same way.

- **507.cactuBSSN_r** and **607.cactuBSSN_s** pass overlapping source and destination buffers to `sprintf`. Their output does not validate.
- **737.gmsh_r** assigns `NULL` to a `bool` in a line SPEC added under `#ifdef SPEC`. musl defines `NULL` as `nullptr` in C++, which does not convert to `bool`; glibc's headers define it as `__null`, which does.
- **748.flightdm_r** takes the GNU `strerror_r`, which returns `char *`, whenever `_GNU_SOURCE` is defined, and otherwise needs `_POSIX_C_SOURCE`. clang++ defines `_GNU_SOURCE` on Linux targets and portable C++ keeps it because libc++'s headers need it against musl (`strtod_l` and the other `_l` functions), so undefining it for the benchmark breaks libc++'s headers, and musl's `strerror_r` is the POSIX one in any case.

767.nest_r and 766.femflow_r relied on glibc and libstdc++ in ways a per-benchmark setting fixes; they are in the [settings table](#portable-settings-in-the-spec-configuration) and pass.

### fork

.NET cannot duplicate a running process, so `fork` and `vfork` fail with `ENOSYS`; `posix_spawn`, `system` and `popen` start host processes instead ([dev/host-abi.md](host-abi.md)).

- **500.perlbench_r, 600.perlbench_s:** the test workload runs tests through a piped `open`, which perl implements with `fork`. The output does not validate.
- **827.cppcheck_s:** the speed workload checks files in child processes (`-j` with the thread count), which cppcheck creates with `fork`.

### Prototypes that contradict the definition

Native code tolerates a call whose prototype disagrees with the callee when the registers happen to line up; llvmnet, which sees both sides of a call into a translated C library or runtime, rejects it (`Incompatible direct call signature`; [dev/calling-convention.md](calling-convention.md#direct-calls)). In system mode these calls reach native code, so the mismatch is invisible there.

- **722.palm_r, 822.palm_s** declare `open` with a fixed four-argument prototype, while the C library defines it as variadic.
- **621.wrf_s** calls `omp_get_thread_num` in `module_fr_fire_util` with the `use omp_lib` and the declaration commented out, so implicit typing makes the result `REAL`: `float ()` against the runtime's `i32 ()`. Natively the caller reads an unrelated register. 521.wrf_r does not compile the line, which sits under an OpenMP sentinel.

### Stack sizes

627.cam4_s needs more than 8 MB of stack on the main thread and on the OpenMP workers (SPEC's guidance is an unlimited stack limit, which `scripts/spec-harness.sh` sets). The main thread's stack follows the limit under JIT and NativeAOT; the workers' follows `RLIMIT_STACK` or `OMP_STACKSIZE`, as with native libomp, since the host layer sizes a thread's managed stack by what `pthread_create` asks for ([dev/host-abi.md](host-abi.md)).

### Portable settings in the SPEC configuration

[config/spec/llvmnet.cfg](../../config/spec/llvmnet.cfg) adds these settings in portable mode only. The settings that apply in both modes are listed in [the benchmark settings](#benchmark-settings).

| Benchmarks | Setting | Reason |
| --- | --- | --- |
| 500.perlbench_r, 600.perlbench_s | `-DSPEC_NO_USE_STDIO_PTR -DSPEC_NO_USE_STDIO_BASE -DI_FCNTL` | With `SPEC_LINUX_X64`, perl reads glibc's private `FILE` fields and assumes that `sys/file.h` includes `fcntl.h`. musl's `FILE` is opaque; these are perl's own switches for both. |
| 525.x264_r, 625.x264_s | `-include string.h -include stdlib.h` | x264's bundled `getopt.c` includes these headers only when it sees glibc's `__GNU_LIBRARY__`. |
| 523.xalancbmk_r, 623.xalancbmk_s | `-D__linux__ -idirafter config/spec/include` | Xerces's platform detection and its `linux/limits.h` ([above](#linux-platform-detection)). |
| 526.blender_r | `-DSPEC_NO_ISFINITE` | Blender calls `std::isfinite` after including only `<math.h>`, which declares it in libstdc++ but not in libc++. The switch, documented by SPEC, makes it call `finite` instead. |
| 710.omnetpp_r | `-DUSE_PORTABLE_COROUTINES` | musl has no `swapcontext`. omnetpp's setjmp-based coroutines only need to link, because the model creates none. |
| 714.cpython_r | `-include sys/types.h` | CPython's pthread stubs need the pthread types, which glibc's `stdlib.h` declares through `sys/types.h`. |
| 777.zstd_r | `-DPLATFORM_POSIX_VERSION=200809L` | zstd detects POSIX through `__unix__`; this is its documented override. |
| 767.nest_r, 867.nest_s | `-include sys/time.h` | NEST's `simulation_manager.h` declares `timeval` members but includes `sys/time.h` only without SPEC's `-DDISABLE_TIMING`; glibc's other headers declare the type anyway, musl's do not. |
| 766.femflow_r | `-DSPEC_NO_CXX17_SPECIAL_MATH_FUNCTIONS` | deal.II calls `std::cyl_bessel_j`, which libc++ does not implement; SPEC's documented switch substitutes Boost's. |
| 723.llvm_r, 823.llvm_s | `-D__linux__ -idirafter config/spec/include` | LLVM's endian and Unix platform code, and the `execinfo.h` its `config.h` includes. |
| 735.gem5_r, 835.gem5_s | `-D__linux__ -include sys/types.h` | libelf's platform configuration, and `mode_t`. |
| 772.marian_r, 872.marian_s | `-D__linux__ -D__unix__` | marian's path library. |

### Reproducing the check

The check needs the licensed kits installed as `artifacts/spec2017-harness` and `artifacts/spec2026-harness`, and a packaged toolchain. It runs through `scripts/spec-harness.sh`, whose requirements are listed in [the scripts](#scripts). The harness passes `config/spec/include`, the headers some settings name, to runcpu as `llvmnet_spec_include`, and the check scripts copy it with the configuration.

`scripts/spec-portable-check.sh baseline.tsv toolchain [glob]` reruns, in portable mode and with a clean build, every entry of a baseline table whose result is not `PASS`, optionally only those matching a benchmark glob. Each run gets a new directory under `artifacts/spec-portable-audit/` (`SPEC_PORTABLE_OUTPUT` changes the root) with copies of the scripts and configuration, toolchain hashes, `results.tsv`, harness logs and the make logs. `SPEC_TIMEOUT` bounds each build and run (default 900 seconds). `scripts/spec-portable-audit.mjs directory` keeps the latest result per benchmark from all runs under a directory and adds each failure's first diagnostic; its output is the next baseline.

```sh
bash scripts/package-toolchain.sh artifacts/spec-toolchain
# A baseline with every installed benchmark, none of them passed yet:
{ printf 'benchmark\tresult\n'
  ls artifacts/spec2017-harness/benchspec/CPU artifacts/spec2026-harness/benchspec/CPU |
      grep -E '^[0-9]{3}\.' | sort -u | sed 's/$/\tNEW/'; } > artifacts/spec-portable-baseline.tsv
SPEC_TIMEOUT=1800 bash scripts/spec-portable-check.sh artifacts/spec-portable-baseline.tsv artifacts/spec-toolchain
node scripts/spec-portable-audit.mjs artifacts/spec-portable-audit > artifacts/spec-portable-current.tsv
# Retry the failures, here only 722.palm_r:
bash scripts/spec-portable-check.sh artifacts/spec-portable-current.tsv artifacts/spec-toolchain '722.*'
```

Do not repackage the toolchain while a check uses it.

`bash tests/portable-spec-causes.sh` checks the causes with small probes and needs no SPEC kit. The `linux/limits.h` and `machine/endian.h` headers, the four-argument `open` and `--system-library` must fail in portable mode with their diagnostic. Probes of headers and library functions that SPEC programs use (`sys/ioctl.h`, `locale.h`, `complex.h`, `omp.h`, `system`, `dup2`, `freopen`, `cexp`, iostreams, `std::thread`, `-funsigned-char`, `-fopenmp` and others) must build and exit like their native builds.
