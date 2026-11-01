# Contributing to llvmnet

## License

By contributing you agree that your contribution is licensed under the MIT License, the license in [LICENSE.TXT](LICENSE.TXT).

## Build

Requirements are listed in [README.md](README.md#install). `scripts/ci/install-packages.sh` installs them on Debian 13, and `.devcontainer/` provides the same environment as a container.

```sh
bash scripts/ensure-built.sh     # solution and sysroots
```

## Run the gates

```sh
tests/run.sh --tag core --tag toolchain --exclude-tag slow   # the CI pull-request gate
tests/run.sh --list                                          # every test with its tags and prerequisites
tests/run.sh smoke driver                                    # selected tests by name
FUZZ_SEEDS=500 tests/run.sh fuzz                             # csmith and YARPGen differential fuzzing
dotnet test tests/LlvmNet.UnitTests -c Release
dotnet format whitespace llvmnet.slnx --verify-no-changes
shellcheck -S error scripts/*.sh scripts/ci/*.sh tests/*.sh bin/llvmnet bin/llvmnet-tool
```

The runner builds the solution once, runs tests in parallel, continues after failures, and writes per-test logs plus `summary.tsv` and JUnit `results.xml` under `artifacts/tests/results/`. Tests whose prerequisites are missing are reported as skipped with the reason. Integration and conformance tests need pinned upstream sources, which `scripts/fetch-sources.sh` downloads and verifies. The nightly CI job also runs the `integration`, `optional`, `fuzz` and `conformance` tags; see [.github/workflows/ci.yml](.github/workflows/ci.yml).

## Add a test

1. Put fixtures under `tests/c`, `tests/cpp`, `tests/fortran` or `tests/ir`.
2. Write `tests/<name>.sh`. Start it with the shared header and declare its tags:

   ```sh
   #!/usr/bin/env bash
   # tags: core cpp aot
   # requires: flang
   source "$(dirname "$0")/lib.sh"
   ```

   `lib.sh` sets strict mode, `root`, `llvmnet`, `checks` and `test_output`, and runs `scripts/ensure-built.sh` unless the runner already did. `tests/run.sh --help` lists the other header keys (`serial`, `after`, `timeout`); [docs/dev/testing.md](docs/dev/testing.md) describes the tags, the oracles and the nightly suites.
3. Compare against a native oracle where possible: build the same fixture with `clang-22` or `flang-22` and diff the outputs. Pure IR tests may use expected-output files instead.
4. Print one `PASS: ...` line per property checked and exit nonzero on any failure.

## Change rules

- Tie each change to a plan item in [docs/plan](docs/plan/README.md) and tick it when done. Unplanned fixes are welcome; say so in the PR.
- Unsupported constructs must fail with a diagnostic or an explicit runtime error.
- Never commit licensed SPEC material, private media or machine-specific paths. Everything generated goes under `artifacts/`; see [docs/dev/artifacts.md](docs/dev/artifacts.md).
- Record user-visible changes in [CHANGELOG.md](CHANGELOG.md).
- Keep C# consistent with the surrounding code and [.editorconfig](.editorconfig). Warnings are errors.
- Driver options live in the table in `src/LlvmNet.Compiler/DriverOptions.cs`; `--help` and the tables of [docs/user/driver.md](docs/user/driver.md) are generated from it (`tests/run.sh driver-reference` regenerates the page). `src/LlvmNet.Compiler/FrontendOptions.cs` is generated from the frontends' `--help` by `scripts/frontend-options.py`; rerun it after an LLVM upgrade (`tests/driver-options.sh` checks it).
