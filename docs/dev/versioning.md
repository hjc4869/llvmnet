# Versioning

## Compiler and runtime

The compiler (`llvmnet`) and `LlvmNet.Runtime` share one version, set by `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`. The .NET SDK appends the git commit to the informational version, so a development build reports `0.3.0-dev+<commit>` and a release build `0.3.0+<commit>`.

- `llvmnet --version` prints that version, the LLVM version loaded into the compiler and the ABI tags it can emit.
- Every emitted assembly carries three `AssemblyMetadata` entries: `llvmnet.abi`, `llvmnet.version` and `llvmnet.llvm`. `LlvmNet.Checks inspect <assembly>` prints them.
- Each emitted image's type initializer calls `AbiContract.Require(abi, compilerVersion)`, which throws before any translated code runs when the loaded runtime is incompatible with the compiler that produced the image.
- `--cil-reference` rejects, at compile time, a managed library compiled by an incompatible llvmnet version or for another ABI.

Compatibility follows Semantic Versioning with the usual 0.x rule: while the major version is 0, the major and minor versions must both match, because any 0.x minor release may change the runtime interface. From 1.0 on, the major version must match.

## Release procedure

1. Set `VersionSuffix` to empty and `VersionPrefix` to the release version in `Directory.Build.props`.
2. Move the `Unreleased` section of `CHANGELOG.md` under the new version heading.
3. Commit and tag `v<version>`. The `release` workflow (`.github/workflows/release.yml`) builds the artifacts from the tagged tree with `scripts/release-artifacts.sh`, checks that a second build reproduces them, signs them and creates the GitHub release ([release-process.md](release-process.md)).
4. Bump `VersionPrefix` to the next minor version and restore `VersionSuffix` to `dev`.

## ABI tags

ABI tags are versioned separately from the compiler. The current tags are `dotnet64-v2` (portable mode) and `system-linux-x64-v1` (system mode). A tag stays fixed for the lifetime of its documented contract, including the portable [host interface](host-abi.md); an incompatible change creates a new tag. `llvmnet -c` records the tag in every object, and the linker rejects inputs whose tag differs from the link's, as well as untagged LLVM input. The runtime refuses to load images of two different ABIs into one process. See [ADR-09](../plan/decisions.md#adr-09-portable-abi-evolution).

## LLVM

Each llvmnet release pins exactly one LLVM release, currently 22.1.8. See [ADR-06](../plan/decisions.md#adr-06-llvm-version-policy).
