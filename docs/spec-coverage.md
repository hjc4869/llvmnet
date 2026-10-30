# SPEC CPU coverage

llvmnet is checked against the SPEC CPU 2017 and CPU 2026 suites in both runtime modes. The suites are licensed and are not part of the repository; install each kit under `artifacts/` (the examples use `artifacts/spec2017-harness` and `artifacts/spec2026-harness`). Current portable-mode failures and their causes are in [spec-portable-limitations.md](spec-portable-limitations.md).

## What a check is

- The benchmark builds with [config/spec/llvmnet.cfg](../config/spec/llvmnet.cfg): `llvmnet-clang`, `llvmnet-clang++` and `llvmnet-flang` from a packaged toolchain, `-O3`, C++14 for CPU 2017 and C++17 for CPU 2026, linked with NativeAOT.
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
| `scripts/spec-portable-check.sh baseline toolchain [glob]` | Reruns the entries of a baseline that did not pass, in portable mode; see [spec-portable-limitations.md](spec-portable-limitations.md#reproducing-the-check). |
| `scripts/spec-portable-audit.mjs directory` | The latest portable result per benchmark, with the first diagnostic of each failure. |
| `scripts/spec-summary.sh results.tsv...` | Combines result tables, oldest first, into one row per benchmark and profile with the system and portable results; a missing mode shows as `MISSING`. |

Each run directory holds frozen copies of the scripts and configuration, the toolchain hashes, `results.tsv`, harness logs, copies of the make logs and, once every selected entry has a result, `completion.txt`. A directory without `completion.txt` is an interrupted run. Keep the toolchain unchanged while a run uses it, and do not run two checks against the same installed kit at once. Environment variables such as `SPEC_TIMEOUT`, `SPEC_THREADS`, `SPEC_CXX_STANDARD`, `SPEC_SYSTEM_CXX` and `SPEC_OPENMP_PREFIX` are described in [toolchain.md](toolchain.md#standard-spec-configuration).

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

The configuration applies these settings in both modes. The settings for portable mode only are in [spec-portable-limitations.md](spec-portable-limitations.md#portable-settings-in-the-spec-configuration).

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

WRF's bundled NetCDF uses `SPEC_CASE_FLAG` for lowercase wrapper names, and its radiation tables are big-endian unformatted files. One WRF source call omits a required argument: `--trap-missing-arguments` turns that call into a run-time trap ([dev/calling-convention.md](dev/calling-convention.md#direct-calls)), which the test input does not reach.

Speed benchmarks do not all run OpenMP workers. 835.gem5_s is single-threaded (its kit metadata adds `-DSPEC_AUTO_SUPPRESS_THREADING`), and 827.cppcheck_s runs its checks in child processes, passing `-j` with the thread count.
