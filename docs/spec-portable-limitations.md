# Portable SPEC limitations

In portable mode (`--runtime=portable`, ABI `dotnet64-v2`), SPEC CPU 2017 and CPU 2026 benchmarks compile against musl, libc++/libc++abi, LLVM's OpenMP runtime and flang-rt from the portable sysroot. This page lists the benchmarks that do not pass in portable mode and why, the settings the SPEC configuration applies for portable mode, and how to rerun the check. A check builds the benchmark at `-O3` with NativeAOT and runs it with test inputs; it passes when runcpu validates the output. [spec-coverage.md](spec-coverage.md) describes the SPEC tooling in general.

| Cause | Benchmarks |
| --- | --- |
| [glibc behavior that musl does not share](#glibc-behavior) | 507.cactuBSSN_r, 607.cactuBSSN_s, 737.gmsh_r, 748.flightdm_r |
| [`fork`](#fork) | 500.perlbench_r, 600.perlbench_s, 827.cppcheck_s |
| [Prototypes that contradict the definition](#prototypes-that-contradict-the-definition) | 722.palm_r, 822.palm_s, 621.wrf_s |

All other CPU 2017 and CPU 2026 entries pass in portable mode, some with the [portable settings](#portable-settings-in-the-spec-configuration) below. For portable parity with system mode ([ADR-14](plan/decisions.md#adr-14-linux-identity-of-the-portable-target)), every entry in the table is a listed exclusion: programs that need `fork`, programs that rely on glibc behavior musl does not share and fail natively against musl too, and prototypes that contradict the definition; each needs a source change the SPEC rules do not allow.

## OpenMP

Portable mode links LLVM's OpenMP runtime from the sysroot (FT-08, `ports/openmp`), so with more than one thread the SPEC configuration builds the kits' OpenMP benchmarks with `-fopenmp -DSPEC_OPENMP` and lowers CPU 2026 Fortran `DO CONCURRENT` loops to OpenMP (`-fdo-concurrent-to-openmp=host`) in both modes; `--system-library` is only for the native runtime in system mode. The speed benchmarks that need OpenMP run on four workers over musl's pthreads; those that use C++ threads or pthreads instead run the same way. The entries that do not pass are listed above by cause.

## Linux platform detection

Portable code sees neither `__linux__` nor `__unix__` ([ADR-14](plan/decisions.md#adr-14-linux-identity-of-the-portable-target)). Programs that choose their platform code by those macros build with them as a per-benchmark setting, which makes the program, not llvmnet, responsible for the Linux interfaces it then uses. With the settings in the table below, all of these pass:

- **523.xalancbmk_r, 623.xalancbmk_s:** Xerces detects its platform through `__linux__`, and its Linux platform file, selected by `-DSPEC_LINUX`, includes the kernel header `linux/limits.h` for `PATH_MAX`. The portable sysroot has no kernel headers; `config/spec/include/linux/limits.h` includes `<limits.h>` instead.
- **723.llvm_r, 823.llvm_s:** `SwapByteOrder.h` includes the BSD header `machine/endian.h` when it does not detect Linux. The benchmark's prebuilt `config.h` also includes glibc's `execinfo.h`, whose functions it calls only with backtraces enabled, which they are not; `config/spec/include/execinfo.h` declares them.
- **735.gem5_r, 835.gem5_s:** gem5's libelf defines `roundup2` and its byte order only for the systems it recognizes, such as glibc and Linux. gem5 also uses `mode_t` where only glibc's headers have declared it.
- **772.marian_r, 872.marian_s:** marian's path library stops with `#error Unsupported system.` without `__unix__`, and finds the executable's path through `/proc/self/exe` only under `__linux__`.

## glibc behavior

These programs rely on glibc behavior that no flag selects; built natively against musl, they fail the same way.

- **507.cactuBSSN_r** and **607.cactuBSSN_s** pass overlapping source and destination buffers to `sprintf`. Their output does not validate.
- **737.gmsh_r** assigns `NULL` to a `bool` in a line SPEC added under `#ifdef SPEC`. musl defines `NULL` as `nullptr` in C++, which does not convert to `bool`; glibc's headers define it as `__null`, which does.
- **748.flightdm_r** takes the GNU `strerror_r`, which returns `char *`, whenever `_GNU_SOURCE` is defined, and otherwise needs `_POSIX_C_SOURCE`. clang++ defines `_GNU_SOURCE` on Linux targets and portable C++ keeps it because libc++'s headers need it against musl (`strtod_l` and the other `_l` functions), so undefining it for the benchmark breaks libc++'s headers, and musl's `strerror_r` is the POSIX one in any case.

767.nest_r and 766.femflow_r relied on glibc and libstdc++ in ways a per-benchmark setting fixes; they are in the [settings table](#portable-settings-in-the-spec-configuration) and pass.

## fork

.NET cannot duplicate a running process, so `fork` and `vfork` fail with `ENOSYS`; `posix_spawn`, `system` and `popen` start host processes instead ([dev/host-abi.md](dev/host-abi.md)).

- **500.perlbench_r, 600.perlbench_s:** the test workload runs tests through a piped `open`, which perl implements with `fork`. The output does not validate.
- **827.cppcheck_s:** the speed workload checks files in child processes (`-j` with the thread count), which cppcheck creates with `fork`.

## Prototypes that contradict the definition

Native code tolerates a call whose prototype disagrees with the callee when the registers happen to line up; llvmnet, which sees both sides of a call into a translated C library or runtime, rejects it (`Incompatible direct call signature`; [dev/calling-convention.md](dev/calling-convention.md#direct-calls)). In system mode these calls reach native code, so the mismatch is invisible there.

- **722.palm_r, 822.palm_s** declare `open` with a fixed four-argument prototype, while the C library defines it as variadic.
- **621.wrf_s** calls `omp_get_thread_num` in `module_fr_fire_util` with the `use omp_lib` and the declaration commented out, so implicit typing makes the result `REAL`: `float ()` against the runtime's `i32 ()`. Natively the caller reads an unrelated register. 521.wrf_r does not compile the line, which sits under an OpenMP sentinel.

## Stack sizes

627.cam4_s needs more than 8 MB of stack on the main thread and on the OpenMP workers (SPEC's guidance is an unlimited stack limit, which `scripts/spec-harness.sh` sets). The main thread's stack follows the limit under JIT and NativeAOT; the workers' follows `RLIMIT_STACK` or `OMP_STACKSIZE`, as with native libomp, since the host layer sizes a thread's managed stack by what `pthread_create` asks for ([dev/host-abi.md](dev/host-abi.md)).

## Portable settings in the SPEC configuration

[config/spec/llvmnet.cfg](../config/spec/llvmnet.cfg) adds these settings in portable mode only. The settings that apply in both modes are listed in [spec-coverage.md](spec-coverage.md#benchmark-settings).

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

## Reproducing the check

The check needs the licensed kits installed as `artifacts/spec2017-harness` and `artifacts/spec2026-harness`, and a packaged toolchain. It runs through `scripts/spec-harness.sh`, whose requirements are listed in [spec-coverage.md](spec-coverage.md#scripts). The harness passes `config/spec/include`, the headers some settings name, to runcpu as `llvmnet_spec_include`, and the check scripts copy it with the configuration.

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
