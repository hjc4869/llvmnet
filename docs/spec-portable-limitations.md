# Portable SPEC limitations

In portable mode (`--runtime=portable`, ABI `dotnet64-v2`), SPEC CPU 2017 and CPU 2026 benchmarks compile against musl, libc++/libc++abi and flang-rt from the portable sysroot. This page lists the benchmarks that do not pass in portable mode and why, the settings the SPEC configuration applies for portable mode, and how to rerun the check. A check builds the benchmark at `-O3` with NativeAOT and runs it with test inputs; it passes when runcpu validates the output. [spec-coverage.md](spec-coverage.md) describes the SPEC tooling in general.

| Cause | Benchmarks |
| --- | --- |
| [No portable OpenMP runtime](#openmp) | the speed benchmarks that use OpenMP |
| [Platform code selected by `__linux__`](#linux-platform-detection) | 523.xalancbmk_r, 623.xalancbmk_s, 723.llvm_r, 823.llvm_s, 735.gem5_r, 835.gem5_s, 772.marian_r, 872.marian_s |
| [glibc behavior that musl does not share](#glibc-behavior) | 507.cactuBSSN_r, 737.gmsh_r, 748.flightdm_r, 767.nest_r |
| [`fork`](#fork) | 500.perlbench_r, 600.perlbench_s, 827.cppcheck_s |
| [Portable ABI restrictions](#portable-abi-restrictions) | 526.blender_r, 722.palm_r |
| [C++17 special math functions](#c17-special-math-functions) | 766.femflow_r |

All other CPU 2017 and CPU 2026 entries pass in portable mode.

## OpenMP

The portable sysroot has no OpenMP runtime (FT-08): `omp.h` is not found, and code compiled with `-fopenmp` calls `__kmpc_*` functions that do not resolve. `--system-library`, which links the native LLVM OpenMP runtime, requires `--runtime=system`. With more than one thread, the SPEC configuration builds the kits' OpenMP benchmarks with `-fopenmp -DSPEC_OPENMP` and lowers CPU 2026 Fortran `DO CONCURRENT` loops to OpenMP (`-fdo-concurrent-to-openmp=host`). These speed benchmarks therefore fail:

603.bwaves_s, 607.cactuBSSN_s, 619.lbm_s, 621.wrf_s, 627.cam4_s, 628.pop2_s, 638.imagick_s, 644.nab_s, 649.fotonik3d_s, 654.roms_s, 657.xz_s, 800.pot3d_s, 803.sph_exa_s, 807.ntest_s, 809.cactus_s, 811.tealeaf_s, 816.nab_s, 820.cloverleaf_s, 822.palm_s, 849.fotonik3d_s, 854.graph500_s, 857.namd_s, 865.roms_s, 867.nest_s, 881.neutron_s.

Speed benchmarks that use C++ threads or pthreads instead of OpenMP run on musl's pthreads.

## Linux platform detection

Portable code sees neither `__linux__` nor `__unix__` ([ADR-14](plan/decisions.md#adr-14-linux-identity-of-the-portable-target), awaiting the owner's decision). These programs choose their platform code by those macros:

- **523.xalancbmk_r, 623.xalancbmk_s:** Xerces detects its platform through `__linux__`, and its Linux platform file, selected by `-DSPEC_LINUX`, includes the kernel header `linux/limits.h`, which the portable sysroot does not have.
- **723.llvm_r, 823.llvm_s:** `SwapByteOrder.h` includes the BSD header `machine/endian.h` when it does not detect Linux.
- **735.gem5_r, 835.gem5_s:** gem5's libelf defines `roundup2` only for the systems it recognizes, such as glibc and Linux.
- **772.marian_r, 872.marian_s:** marian's path library stops with `#error Unsupported system.`

Each would need the macros or a per-benchmark workaround.

## glibc behavior

These programs rely on glibc behavior; built natively against musl, they fail the same way.

- **507.cactuBSSN_r** passes overlapping source and destination buffers to `sprintf`. Its output does not validate.
- **737.gmsh_r** assigns `NULL` to a `bool`. musl defines `NULL` as `nullptr` in C++, which does not convert to `bool`.
- **748.flightdm_r** expects the GNU `strerror_r`, which returns `char *` under `_GNU_SOURCE`. musl's returns `int`.
- **767.nest_r** uses `struct timeval` without including `sys/time.h`, relying on glibc's other headers to declare it.

## fork

.NET cannot duplicate a running process, so `fork` and `vfork` fail with `ENOSYS`; `posix_spawn`, `system` and `popen` start host processes instead ([dev/host-abi.md](dev/host-abi.md)).

- **500.perlbench_r, 600.perlbench_s:** the test workload runs tests through a piped `open`, which perl implements with `fork`. The output does not validate.
- **827.cppcheck_s:** the speed workload checks files in child processes (`-j` with the thread count), which cppcheck creates with `fork`.

## Portable ABI restrictions

- **526.blender_r** requires `-funsigned-char`. Plain `char` is signed in `dotnet64-v2`, and the driver rejects the option: `-funsigned-char cannot change the versioned portable ABI`.
- **722.palm_r** declares `open` with a fixed four-argument prototype, while the C library defines it as variadic. The compiler rejects the call (`Incompatible direct call signature for open`); see [dev/calling-convention.md](dev/calling-convention.md#direct-calls). 822.palm_s fails on OpenMP first.

## C++17 special math functions

**766.femflow_r** calls `std::cyl_bessel_j`. libc++ does not implement the C++17 special math functions.

## Portable settings in the SPEC configuration

[config/spec/llvmnet.cfg](../config/spec/llvmnet.cfg) adds these settings in portable mode only. The settings that apply in both modes are listed in [spec-coverage.md](spec-coverage.md#benchmark-settings).

| Benchmarks | Setting | Reason |
| --- | --- | --- |
| 500.perlbench_r, 600.perlbench_s | `-DSPEC_NO_USE_STDIO_PTR -DSPEC_NO_USE_STDIO_BASE -DI_FCNTL` | With `SPEC_LINUX_X64`, perl reads glibc's private `FILE` fields and assumes that `sys/file.h` includes `fcntl.h`. musl's `FILE` is opaque; these are perl's own switches for both. |
| 525.x264_r, 625.x264_s | `-include string.h -include stdlib.h` | x264's bundled `getopt.c` includes these headers only when it sees glibc's `__GNU_LIBRARY__`. |
| 710.omnetpp_r | `-DUSE_PORTABLE_COROUTINES` | musl has no `swapcontext`. omnetpp's setjmp-based coroutines only need to link, because the model creates none. |
| 714.cpython_r | `-include sys/types.h` | CPython's pthread stubs need the pthread types, which glibc's `stdlib.h` declares through `sys/types.h`. |
| 777.zstd_r | `-DPLATFORM_POSIX_VERSION=200809L` | zstd detects POSIX through `__unix__`; this is its documented override. |

## Reproducing the check

The check needs the licensed kits installed as `artifacts/spec2017-harness` and `artifacts/spec2026-harness`, and a packaged toolchain. It runs through `scripts/spec-harness.sh`, whose requirements are listed in [spec-coverage.md](spec-coverage.md#scripts).

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

`bash tests/portable-spec-causes.sh` checks the causes with small probes and needs no SPEC kit. The `linux/limits.h`, `machine/endian.h` and `omp.h` headers, `-fopenmp`, `-funsigned-char`, the four-argument `open` and `--system-library` must fail in portable mode with their diagnostic. Probes of headers and library functions that SPEC programs use (`sys/ioctl.h`, `locale.h`, `complex.h`, `system`, `dup2`, `freopen`, `cexp`, iostreams, `std::thread` and others) must build and exit like their native builds.
