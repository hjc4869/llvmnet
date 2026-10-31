# Evidence: portable SPEC settings for BE-41 and LC-31 (2026-10-31)

Host: Linux x86-64 (Debian 13), 32 logical CPUs, .NET SDK 10.0.403, LLVM/Clang/Flang 22.1.8. Every result below was produced by a command in this repository; licensed SPEC material is not included.

## What changed

- BE-41 (`d2dc451`): portable mode accepts `-funsigned-char` (ADR-09). 526.blender_r also needs SPEC's `-DSPEC_NO_ISFINITE` in portable mode, because libc++'s `<math.h>` does not declare `std::isfinite`.
- LC-31: the entries that select platform code by `__linux__` build with per-benchmark settings in portable mode (ADR-14), listed in [dev/spec.md](../dev/spec.md#portable-settings-in-the-spec-configuration). Two headers in `config/spec/include` stand in for what portable mode does not have: `linux/limits.h` (xalancbmk) and a declarations-only `execinfo.h` (LLVM). The driver now forwards `-idirafter <dir>`.

## SPEC CPU checks in portable mode

`scripts/spec-portable-check.sh` with the toolchain packaged by `scripts/package-toolchain.sh artifacts/spec-portable-toolchain`: clean build at `-O3` with NativeAOT, test inputs, one copy, four threads for speed entries; an entry passes when runcpu validates its output.

| Entry | First run | Cause found | Final result |
| --- | --- | --- | --- |
| 526.blender_r | build failure | `std::isfinite` after `<math.h>` (libc++) | PASS with `-funsigned-char -DSPEC_NO_ISFINITE` |
| 523.xalancbmk_r, 623.xalancbmk_s | build failure | the driver did not take `-idirafter`'s argument | PASS with `-DSPEC_LINUX -D__linux__ -idirafter config/spec/include` |
| 723.llvm_r, 823.llvm_s | build failure | `config.h` includes glibc's `execinfo.h` | PASS with `-D__linux__ -idirafter config/spec/include` |
| 735.gem5_r, 835.gem5_s | build failure | `mode_t` used without `sys/types.h` | PASS with `-D__linux__ -include sys/types.h` |
| 772.marian_r, 872.marian_s | PASS | | PASS with `-D__linux__ -D__unix__` |

With these, 67 of the 101 CPU 2017 and CPU 2026 entries pass in portable mode, against 58 in the LC-23 audit. The other 34: 25 need OpenMP (FT-08), 4 rely on glibc behavior, 1 uses C++17 special math functions, and 4 are parity exclusions under ADR-14 (`fork`: 500.perlbench_r, 600.perlbench_s, 827.cppcheck_s; portable ABI: 722.palm_r).

## Tests

| Gate | Result |
| --- | --- |
| `tests/run.sh -j 8` (core tag) after BE-41 | 69 passed, 0 failed, 359 s |
| `tests/run.sh -j 8` (core tag) after the driver change | 69 passed, 0 failed, 302 s |
| `tests/modes.sh` | a `-funsigned-char` object linked with a signed-`char` one prints what the same objects print natively at `-O0` |
| `tests/portable-spec-causes.sh` | 18 causes resolved and matching native, 6 still rejected with their diagnostic |
| `tests/libcxx-conformance.sh` on libc++'s two `-funsigned-char` tests | `formatter.char.funsigned-char.pass.cpp` and `make_format_args.sh.cpp` pass |
| `tests/driver.sh`, `tests/spec-matrix.sh` | pass, including `-idirafter <dir>` and the snapshot of `config/spec/include` |
