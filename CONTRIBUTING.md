# Contributing to llvmnet

## License

By contributing you agree that your contribution is licensed under the Apache License v2.0 with LLVM Exceptions, the license in [LICENSE.TXT](LICENSE.TXT).

## Build

Requirements are listed in [README.md](README.md#build). On a Debian-based host with LLVM 22 and the .NET 10 SDK installed:

```sh
dotnet build llvmnet.slnx -c Release
```

## Run the gates

```sh
tests/run.sh --tag core          # PR gate: compiler and runtime regressions, both ABIs, JIT and NativeAOT
tests/run.sh --list              # every test with its tags and prerequisites
tests/run.sh smoke driver        # selected tests by name
FUZZ_SEEDS=500 tests/run.sh fuzz # csmith differential fuzzing (nightly; failures kept with a repro.sh)
dotnet test tests/LlvmNet.UnitTests -c Release
```

The runner builds the solution once, runs tests in parallel, continues after failures, and writes per-test logs plus `summary.tsv` and JUnit `results.xml` under `artifacts/tests/results/`. Tests whose prerequisites are missing are reported as skipped with the reason. Integration tests need pinned upstream sources, which `scripts/fetch-sources.sh` downloads and verifies.

## Add a test

1. Put fixtures under `tests/c`, `tests/cpp`, `tests/fortran` or `tests/ir`.
2. Write `tests/<name>.sh`. Start it with the shared header and declare its tags:

   ```sh
   #!/usr/bin/env bash
   # tags: core cpp aot
   # requires: flang
   source "$(dirname "$0")/lib.sh"
   ```

   `lib.sh` sets `root`, `llvmnet`, `checks` and `test_output`, and builds the solution only when the script runs outside the runner.
3. Compare against a native oracle where possible: build the same fixture with `clang-22` or `flang-22` and diff the outputs. Pure IR tests may use expected-output files instead.
4. Print one `PASS: ...` line per property checked and exit nonzero on any failure.

## Change rules

- Tie each change to a plan item in [docs/plan](docs/plan/README.md) and tick it when done. Unplanned fixes are welcome; say so in the PR.
- Unsupported constructs must fail with a diagnostic or an explicit runtime error. Never stub an operation so that a workload appears to pass.
- Never commit licensed SPEC material, private media or machine-specific paths. Everything generated goes under `artifacts/`; see [docs/dev/artifacts.md](docs/dev/artifacts.md).
- Record user-visible changes in [CHANGELOG.md](CHANGELOG.md).
- Keep C# consistent with the surrounding code and [.editorconfig](.editorconfig). Warnings are errors.
