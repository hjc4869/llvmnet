# Gates

Which tests run when, and what each must reach to pass. The workflows are `.github/workflows/ci.yml` (pull requests, pushes and the nightly schedule) and `.github/workflows/release.yml` (a version tag); the selections are `tests/run.sh` tags ([testing.md](testing.md)). A suite's pass rate counts only the tests that pass natively (clang-22, flang-22, glibc), so it measures parity with a native build. The thresholds are set in the workflow; they are proposals for the owner until accepted ([decisions](../plan/decisions.md#proposed-for-the-owner-2026-11-01-m9)).

## Pull request

Job `pr-gate` on `ubuntu-24.04` in the Debian 13 container, about 15 minutes with four jobs. Every step must pass.

| Step | What | Passes when |
| --- | --- | --- |
| Format | `dotnet format whitespace --verify-no-changes` | no change |
| Unit tests | `dotnet test tests/LlvmNet.UnitTests` | all pass |
| Shell scripts | `shellcheck -S error` over `scripts/`, `tests/` and `bin/` | no error |
| Core and toolchain | `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 4` | every test passes; a test whose prerequisite is missing is skipped with the reason |

The `core` tag holds the compiler and runtime regressions in both modes, the generated pages (support matrix, limitations, driver reference), the cross-platform bundle on Linux (`xp`) and the benchmark harness's self-test; `toolchain` the packaging, the CMake toolchain file, NativeAOT provisioning and the dotnet publish forms.

## Nightly

Four jobs run on the schedule (and on `workflow_dispatch`): integration, conformance, FATE and the `xp` matrix. They are independent of each other, and each fails on any failure below.

| Job | Suites | Passes when |
| --- | --- | --- |
| `nightly` (integration) | unit tests; `--tag core --tag toolchain --tag integration --tag optional` (with `slow`: the full package and deployment test and the release reproducibility test); ffmpeg's browser profile, threaded decoding and SIMD128 profile in both ABIs under the JIT and NativeAOT (WG-05) | every test passes |
| | differential fuzzing (FD-16): 500 new csmith and YARPGen seeds per generator | no mismatch with the native build |
| | benchmarks (PF-01) | no confirmed regression against `tests/perf/baseline.tsv` ([benchmarks.md](benchmarks.md)) |
| `conformance` | the suites in the next table | each at or above its threshold |
| `fate` | ffmpeg's FATE suite (WG-04): ffmpeg 9.0.2 with every component built natively and in portable mode, `make fate` in both against the samples of `tests/ffmpeg/fate-suite.sha256` | the portable build passes at least `FFMPEG_FATE_THRESHOLD` (99) percent of the tests the native build passes; today all 5,285 |
| `xp` | the portable bundle built by `pr-gate` or `nightly`, under the JIT and as NativeAOT executables, on Windows x64, macOS ARM64 and Linux ARM64 | every fixture prints what the native Linux build printed |

### Conformance thresholds

The `conformance` job sets each threshold as an environment variable; a suite fails when any group it reports falls below it. The current rates are those of [the M9 evidence](../evidence/2026-11-01-m9-workload-gates.md).

| Suite (item) | Test | Variable | Threshold | Current rate | Groups |
| --- | --- | --- | ---: | --- | --- |
| musl libc-test (LC-19, WG-03) | `libc-conformance` | `LIBC_THRESHOLD` | 88 | functional 89.6, regression 88.1, math 98.5, api 100 | per group |
| libc++ lit suite (CX-08, WG-03) | `libcxx-conformance` | `LIBCXX_THRESHOLD` | 97 | 97.8 to 100 (input.output lowest) | per directory, without death tests |
| libc++ under NativeAOT (CX-08 subset) | `libcxx-conformance` with `LIBCXX_NATIVEAOT=1` | `LIBCXX_THRESHOLD` | 97 | 98.8 to 100 (futures lowest) | per directory |
| FCVS and gfortran tests (FT-06, WG-03) | `fortran-conformance` | `FORTRAN_THRESHOLD` | 99 | FCVS 100, gfortran 99.8 | per suite |
| flang-rt unit tests (FT-06, WG-03) | `flang-rt-unittests` | `FLANG_RT_UNITTESTS_THRESHOLD` | 100 | 100 (216 of 216) | without the tests that need `fork` and those not compiled for the portable target |
| c-testsuite (WG-02) | `c-testsuite` | `C_TESTSUITE_THRESHOLD` | 99 | portable 99.5, system 100 | per mode |
| llvm-test-suite and gcc-c-torture (WG-01, WG-02) | `llvm-test-suite` | `LLVM_TEST_SUITE_THRESHOLD` | 98 | portable 99.1, system 98.8, NativeAOT subset 99.3 | per mode, without the failures classified as not applicable or documented exclusions; any unclassified failure fails |

`tests/conformance/llvm-test-suite.tsv` names the class of every known llvm-test-suite failure. Two kinds are left out of the adjusted rate: tests of the x86 instruction set in portable mode, which has none by design, and the owner's documented exclusions (`fork`, glibc or libstdc++ behavior that musl and libc++ do not share, prototypes that contradict the definition), as for SPEC. llvmnet's own limitations and open plan items count against the rate. A new failure, or a known one that changes class, must be classified in the same change that causes it.

## Release

The `release` workflow on a `v*` tag runs the unit tests and `tests/run.sh --tag core --tag toolchain` including `slow`, builds the artifacts twice and checks that the sums agree, signs them and publishes the release ([release-process.md](release-process.md)). A release candidate additionally needs the M9 exit: the workload gates (WG-01 to WG-06) at threshold on the candidate build for two consecutive nightly runs.

## Private runner

SPEC CPU 2017 and 2026 (WG-06) cannot run on public CI: the kits are licensed. A self-hosted runner (FD-21) builds and runs every installed entry in both modes ([spec.md](spec.md)); only summarized results leave it. A release candidate needs a completed matrix record for its toolchain build.
