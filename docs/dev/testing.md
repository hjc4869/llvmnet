# Testing

Every claim about llvmnet is backed by a test or a dated evidence file, and the tests are the definition of done: conformance suites, native oracles and workload gates rather than "the workload we tried last passed". This page is the map of the test infrastructure; [CONTRIBUTING.md](../../CONTRIBUTING.md) says how to add a test.

## The runner

`tests/run.sh` discovers `tests/*.sh` scripts with a `# tags:` header, builds the solution and the sysroots once (`scripts/ensure-built.sh`), runs the parallel tests with `-j` jobs and then the serial ones, continues after failures, and writes `artifacts/tests/results/` with one log per test, `summary.tsv` and a JUnit `results.xml`. A test whose prerequisite (`# requires: flang openmp ...`) is missing is reported as skipped with the reason.

```sh
tests/run.sh --list                                          # every test with its tags and prerequisites
tests/run.sh --tag core --tag toolchain --exclude-tag slow   # the pull-request gate
tests/run.sh smoke driver link-inputs                        # selected tests by name
tests/run.sh --tag integration                               # zlib, libxml2, dav1d and ffmpeg from pinned sources
tests/run.sh --tag conformance                               # musl libc-test, the libc++ suite, FCVS and gfortran tests
FUZZ_SEEDS=500 tests/run.sh fuzz                             # csmith and YARPGen differential fuzzing
dotnet test tests/LlvmNet.UnitTests -c Release               # the unit tests (under a second)
```

Header keys: `tags` (selection), `requires` (prerequisites), `serial: N` (run alone after the parallel phase, in order), `after: name` (skip when that test did not pass in this run), `timeout: seconds`. `tests/lib.sh` is the shared prologue: strict mode, `root`, `llvmnet`, `checks` (the `LlvmNet.Checks` tool) and `test_output` (`artifacts/tests/<name>/`).

## What the tags cover

| Tag | Contents | When it runs |
| --- | --- | --- |
| `core` | Compiler and runtime regressions in both modes: the driver and its option sweep, link inputs, optimization levels, language options, diagnostics, IR semantics probes against native (`semantics`), C, C++ and Fortran fixtures, the host interface against native (`host`), JIT validation of every emitted assembly (`jit-validation`, serial), the support matrix and limitations pages, the cross-platform bundle on this host (`xp`) | every pull request |
| `toolchain` | Packaging and deployment: the relocatable package, the dotnet publish forms, NativeAOT provisioning (`aot-offline`), the driver reference | every pull request (without `slow`) |
| `aot` | Tests that also publish NativeAOT executables | with their other tags |
| `cpp`, `fortran` | Need `clang++-22` or `flang-22` | with their other tags |
| `slow` | The full package and deployment test (`deployment`), the release reproducibility test (`release`) | nightly |
| `integration` | zlib, libxml2, dav1d and the ffmpeg profiles from pinned sources, decoded media compared with native builds | nightly |
| `optional` | System-mode libstdc++ and OpenMP, SIMD128, local media (`FFMPEG_TEST_VIDEO`) | nightly where the prerequisites exist |
| `conformance` | musl's libc-test, the libc++ and libc++abi lit suites, FCVS and the gfortran tests of llvm-test-suite | nightly |
| `fuzz` | csmith and YARPGen programs compiled natively and with llvmnet, outputs compared, failures kept with a `repro.sh` | nightly, 500 seeds per generator |
| `spec` | SPEC CPU 2017 and 2026 through the standard harness ([spec.md](spec.md)); licensed kits, never in public CI | a private runner |

The CI workflow (`.github/workflows/ci.yml`) runs the pull-request gate in the Debian 13 container that `scripts/ci/install-packages.sh` provisions, the nightly job on a schedule (ending with the benchmark harness, [benchmarks.md](benchmarks.md), which fails the job on a confirmed regression), and the `xp` jobs on Windows, macOS and Linux ARM64 runners with the bundle the Linux job built. The gate's wall time with four jobs is under 15 minutes.

## Oracles

- **Native builds.** Most fixture tests build the same source with `clang-22`, `clang++-22` or `flang-22` and compare stdout and the exit status; `LlvmNet.Checks compare-numeric` compares numeric output within a tolerance where native libm and musl differ by an ulp.
- **Expected files.** IR tests (`tests/ir/*.ll`) carry their expected output, so they need no native oracle.
- **Upstream suites.** libc-test, the libc++ lit suite (through `llvmnet-clang++` and the launcher), FCVS and the gfortran tests run unmodified; each test records its pass counts and failure list under `artifacts/tests/<name>/`, and the nightly job uploads them.
- **The frozen ABI.** `tests/abi-freeze.sh` compares the portable sysroot's header stamp and the data layout `tests/abi/layout.c` prints with their frozen values, and links an object compiled at the freeze with a new program ([user/abi.md](../user/abi.md)).
- **JIT validation.** `LlvmNet.Checks jit-all` forces the JIT to compile every method of every assembly the run emitted, so invalid IL fails even on paths no test executes; `ilverify` cannot be used, because translated code is unverifiable by design (raw pointers, `localloc`, `calli`).
- **Generated pages.** `tests/support-matrix.sh`, `tests/driver-reference.sh` and `tests/limitations.sh` regenerate `docs/user/support-matrix.md`, the tables of `docs/user/driver.md` and `docs/user/limitations.md` and fail when the committed page differs, so the documentation is produced by the code it describes.

## Fuzzing

`tests/fuzz.sh` (FD-16) generates one csmith and one YARPGen program per seed, cycles `-O0` to `-O3` by seed, and compares the output of llvmnet's system and portable builds with a native `clang-22` build; a failing program is kept under `artifacts/tests/fuzz/failures/` with a `repro.sh`. The script header lists its variables (`FUZZ_SEEDS`, `FUZZ_FIRST_SEED`, `FUZZ_GENERATORS`, `FUZZ_OPT`, `FUZZ_RUNTIMES`, `FUZZ_MEMORY_MB`, `JOBS`).

Every program runs with a time limit and a resident memory limit, because about one YARPGen seed in five declares arrays of several GB, some of hundreds; without limits, a 16-job run together with libc-test once exhausted a 124 GB host. A native program gets 10 s and `FUZZ_MEMORY_MB` (default 2048). One whose static data alone is larger is skipped before it runs, one that grows past the limit is stopped and skipped, and `results.tsv` gives the reason. The llvmnet builds get 300 s and 512 MB more for the .NET runtime, which needs about 50 MB beyond the native program; a build that still exceeds its limit fails like a hang. A watchdog reads the program's resident set every 20 ms and kills it at the limit. `ulimit -v` cannot limit `dotnet`, whose runtime reserves 256 GB of address space and, when that is limited, needs gigabytes more than it touches; cgroups need a user systemd, which CI's container does not have.

`JOBS` defaults to half the processors, at most four. With the defaults a run stays under about `JOBS` × 3 GB: 6 GB with the two jobs of the 4-core, 16 GB nightly runner. On a shared workstation, run it in a capped scope as well: `systemd-run --user --scope -p MemoryMax=24G -p MemorySwapMax=0 tests/run.sh fuzz`.

## Unit tests

`tests/LlvmNet.UnitTests` (xUnit) covers the managed components that need no frontend: `Float80` against x87 encodings, wide integers, the response-file tokenizer, the driver's option table and its rejections, build and ABI compatibility checks, the Windows path forms of the host layer, generated zoneinfo. They run in well under a second and are part of the gate.

## Evidence

Measurements and run results that back a plan item go to `docs/evidence/<date>-<topic>.md`, with the command that produced each number. The plan ([plan/README.md](../plan/README.md)) ticks an item only with a link to such a file or a test.
