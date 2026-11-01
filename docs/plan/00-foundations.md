# Workstream FD: Foundations (repository, build, tests, CI)

Prefix `FD`. Owner: unassigned. Primary milestone: [M0](README.md#m0-foundations).

This workstream makes every later change reviewable and verifiable: a license, a reproducible build, a test runner with machine-readable results, and a CI gate. Nothing here changes compiler behavior.

## Current state (verified 2026-10-30)

- Five commits, all from 2026-09-07/08, one author. No `LICENSE`, `CONTRIBUTING`, `CHANGELOG`, issue templates, or CI configuration. The project cannot legally be redistributed or contributed to until a license exists.
- Build: `dotnet build llvmnet.slnx -c Release` succeeds with zero warnings on SDK 10.0.403 / runtime 10.0.14 (the docs were last verified on 10.0.400 / 10.0.11). The native LLVM bridge is built by `scripts/build-native.sh` from an MSBuild `Exec` target, keyed on file timestamps, and requires `clang++-22` and `llvm-config-22` on `PATH`.
- Tests: 79 Bash scripts (2,206 lines) plus 4,111 lines of C/C++/Fortran/LLVM IR fixtures. `tests/all.sh` runs them sequentially under `set -e`, so the first failure aborts the run and there is no per-test result record. Every script re-invokes `dotnet build`. `tests/LlvmNet.Checks` provides `inspect` (IL-only and P/Invoke count), `compare-numeric` and a SIMD probe. There are no unit tests for the compiler or runtime classes.
- External inputs default to machine-specific paths: ffmpeg from `/mnt/storage/david/Development/LightPlayer-Packages`, test media from `/home/david/Videos/*.mp4`, licensed SPEC kits under `artifacts/`. A new contributor cannot run the integration tags.
- The version string `llvmnet 0.2.0` is hard-coded in `Driver.cs`. Emitted assemblies carry only the ABI tag, not the compiler version.
- `tests/smoke.sh` and `tests/driver.sh` pass today. The full `tests/all.sh core` result for 2026-10-30 is recorded in the [README status snapshot](README.md#status-snapshot-2026-10-30).

## Items

Legend: size S (about a day), M (about a week), L (several weeks), XL (needs its own breakdown). Priority P0 blocks 1.0, P1 should ship in 1.0, P2 is a post-1.0 candidate. Mark done with `[x]` and append the PR or commit.

### Repository and governance

- [x] **FD-01** (S, P0) Choose and add a `LICENSE` at the repository root, state it in `README.md`, and copy it in `scripts/package-toolchain.sh`. Done when the license file exists, the packaged toolchain contains it, and `tests/deployment.sh` checks for it. — **Done** (`8c73733`): MIT, in `LICENSE.TXT` (changed from Apache-2.0 WITH LLVM-exception on 2026-10-30, ADR-13); packaged and checked by `tests/deployment.sh`.
- [x] **FD-02** (S, P0) Add `THIRD-PARTY-NOTICES.md` covering everything redistributed or bundled: LLVM/Clang/Flang binaries and resource headers (Apache-2.0 with LLVM exception), Debian libc++ headers (same), MathNet.Numerics (MIT) while it is still referenced, the optional libstdc++ system archive (GPL-3.0 with GCC Runtime Library Exception, with its obligations spelled out), and later musl, libc++, libc++abi and flang-rt. Done when the package script installs it and a test checks its presence. — **Done** (`8c73733`): `THIRD-PARTY-NOTICES.md`; the package installs it and the Debian copyright file of every bundled LLVM package.
- [x] **FD-03** (S, P1) Add `CONTRIBUTING.md` (how to build, run the gates, add a test, update docs), `.editorconfig` codifying the existing dense C# style, and issue/PR templates that ask for the plan item ID. — **Done** (`8c73733`): `CONTRIBUTING.md`, `.editorconfig`, PR and issue templates.
- [x] **FD-04** (S, P1) Start `CHANGELOG.md` in Keep-a-Changelog form at 0.2.0 and require an entry per user-visible change. — **Done** (`8c73733`): `CHANGELOG.md`.
- [x] **FD-05** (S, P0) Versioning: derive the compiler version from git (MinVer or Nerdbank.GitVersioning) into `AssemblyInformationalVersion`; `--version` prints it; emitted assemblies record compiler version, LLVM version and ABI tag as assembly metadata; `LlvmNet.Runtime` refuses to load an image emitted by a different major version. Write the policy in `docs/dev/versioning.md`: SemVer for the compiler, `dotnet64-v1` frozen at 1.0 and only replaced by `v2`. — **Done** (`ad3d6b2`): Version from `Directory.Build.props` with the commit appended by the SDK (no extra package); `llvmnet.version`/`llvmnet.llvm` metadata; runtime compatibility check; policy in `docs/dev/versioning.md`.
- [x] **FD-06** (M, P0) Remove machine-specific defaults. Add `scripts/fetch-sources.sh` that downloads pinned upstream tarballs (ffmpeg, dav1d, libxml2, zlib, later musl/libc++/flang-rt) by URL and SHA-256 into `artifacts/vendor/`, and make every build script default to those. The private 4K videos become an opt-in local gate selected by `LLVMNET_LOCAL_MEDIA`. Done when a fresh clone on another machine can run `tests/all.sh integration` after `fetch-sources.sh`. — **Done** (`429cc5d, 8b25d9a`): `scripts/sources.lock` and `scripts/fetch-sources.sh`; libc++ headers by URL and SHA-256; private media opt-in through `FFMPEG_TEST_VIDEO`/`LLVMNET_LOCAL_MEDIA`.
- [x] **FD-07** (S, P1) Enable `AnalysisLevel=latest-recommended`, `dotnet format --verify-no-changes` in CI, and keep `TreatWarningsAsErrors`. — **Done** (`dc70940`): Whitespace formatting enforced in CI; analyzer level not raised yet.
- [x] **FD-08** (S, P1) Document the `artifacts/` layout (what is cache, what is evidence, what is disposable) and add `scripts/clean.sh`. — **Done** (`8c73733`): `docs/dev/artifacts.md`, `scripts/clean.sh`.

### Build system

- [x] **FD-09** (M, P0) Make the native bridge build reproducible and portable: accept `LLVMNET_LLVM_PREFIX`/`LLVM_CONFIG`, fail with an actionable message when LLVM 22 development files are missing, key rebuilds on a content hash instead of timestamps, and prepare for non-Linux hosts (CMake instead of a Bash one-liner). Done when `dotnet build` on a machine without LLVM dev packages prints one clear error. — **Done** (`817c4cd`): Checked `LLVM_CONFIG`/`LLVMNET_CXX`, content hash, `native/CMakeLists.txt` alternative.
- [ ] **FD-10** (S, P1) Pin the SDK band in `global.json` deliberately (currently `10.0.100` with `latestFeature`), document the supported SDK/runtime range, and test the minimum and the latest patch in CI.
- [x] **FD-11** (M, P1) One-command bootstrap `scripts/bootstrap.sh` (checks tool versions, stages libc++ headers, builds, runs smoke) and a `Dockerfile`/devcontainer with the pinned Debian + LLVM 22 + .NET 10 image that CI also uses. — **Done** (`837add8, e92854f`): `.devcontainer/Dockerfile` and `scripts/ci/install-packages.sh`, validated by running the PR gate in the image.

### Test infrastructure

- [x] **FD-12** (L, P0) Test runner. Replace the sequential `tests/all.sh` with a runner (Bash or a small C# `LlvmNet.Tests` host) that discovers test scripts, runs them in parallel with isolated output directories, continues after failures, writes per-test status/duration/log and a JUnit XML summary, and supports tags (`core`, `aot`, `native-oracle`, `cpp`, `fortran`, `integration`, `ffmpeg`, `spec`, `slow`) and skip-with-reason when a prerequisite (flang, SPEC kit, local media, OpenMP stage) is absent. Done when `tests/run.sh --tag core` produces `artifacts/tests/results.xml` and a nonzero exit on any failure, and the wall time is recorded. — **Done** (`6f34cce`): `tests/run.sh`: tags, prerequisites, parallel and serial phases, `summary.tsv`, JUnit `results.xml`.
- [x] **FD-13** (M, P0) Build once. Remove `dotnet build` from every test script; the runner builds the solution and exports `LLVMNET` (compiler path), `LLVMNET_CHECKS`, and the fixture root. Expected to cut minutes from the core gate. — **Done** (`6f34cce`): `tests/lib.sh` and `scripts/ensure-built.sh`.
- [x] **FD-14** (M, P0) Unit-test project (`tests/LlvmNet.UnitTests`, xUnit) for pure managed components: `Float80` arithmetic against a host long double oracle, `Formatting`/`Scanning` against known printf/scanf outputs, `VarArgs` layout, `WideInteger`, `TypeSystem` sizes/offsets against LLVM data layout, response-file tokenizer, driver option parsing. Target under 30 seconds; part of the PR gate. — **Done** (`b5f9bde`): 61 xUnit tests.
- [x] **FD-15** (M, P0) IL verification. Run `ilverify` on every assembly the core gate emits (and on `LlvmNet.Runtime.dll`), failing on invalid IL. Keep expected stdout/exit code files for `tests/ir/*.ll` so IR-level tests do not need a native oracle. — **Done** (`6f34cce, 7f61c68`): ILVerify rejects all translated C as unverifiable, so `LlvmNet.Checks jit-all` force-compiles every method instead (`tests/jit-validation.sh`).
- [ ] **FD-16** (M, P1) Differential fuzzing (nightly): csmith and yarpgen programs compiled natively and with llvmnet in system and portable modes, outputs compared, failures minimized with creduce and stored under `tests/regressions/` with the fixing item. — **In progress** (`2f8926f`): `tests/fuzz.sh` (tag `fuzz`) builds pinned csmith and YARPGen, cycles `-O0` to `-O3` by seed, compares system and portable output with native, and keeps each failing program with a `repro.sh`; the nightly job runs 500 new seeds per generator. The first 1,000 seeds per generator found one bug, fixed (BE-35), and one limitation (BE-36). Every program runs under a time and a resident memory limit, so the run fits the 16 GB nightly runner and a shared host ([2026-11-01-fuzz-memory-limits](../evidence/2026-11-01-fuzz-memory-limits.md)). Not yet: automatic reduction (C-Reduce or C-Vise are not in the CI image; the BE-35 fixture was written by hand) and the `tests/regressions/` layout.
- [ ] **FD-17** (S, P1) Flaky-test policy: no automatic retries in gates, per-test time budgets, and a documented quarantine list with an expiry.
- [ ] **FD-18** (M, P1) Fixture licensing: replace non-redistributable media fixtures with FATE samples (or other clearly licensed clips) fetched by hash; keep private media opt-in.

### Continuous integration

- [ ] **FD-19** (M, P0) PR gate on Linux x64 (GitHub Actions or the chosen service): restore/build, unit tests, `core` tag, IL verification, format check, with cached apt LLVM packages and NuGet. Target under 30 minutes. Branch protection requires it. — **In progress** (`837add8`): Workflow written and validated in the CI container locally; it has not run on GitHub, and branch protection is not configured.
- [ ] **FD-20** (M, P1) Nightly: `integration` tag (dependencies, ffmpeg core profile), fuzzing (FD-16), package + deployment test (`tests/toolchain-all.sh`), performance smoke with regression thresholds (PF-01). — **In progress** (`837add8`, `2f8926f`): Nightly job runs the integration and optional tags and 500 fuzzing seeds per generator; performance thresholds are not wired yet.
- [ ] **FD-21** (L, P1) Private runner recipe for licensed SPEC kits (they cannot live in public CI): a documented self-hosted runner setup that publishes only summarized TSV results, matching the existing convention of never committing licensed material.
- [ ] **FD-22** (S, P1) Replace hand-edited status claims with CI-produced evidence: `docs/evidence/` receives dated result tables from nightly runs; README badges link to them.

- [ ] **FD-23** (S, P2) The musl, libc++ and flang-rt tests each build their own copy of the portable runtimes; share one build once the runner supports producer and consumer steps.

## Exit criteria for M0

FD-01, FD-02, FD-05, FD-06, FD-09, FD-12, FD-13, FD-14, FD-15 and FD-19 are done, and the PR gate has been green on three consecutive merges.
