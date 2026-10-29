# Versioning

## Compiler and runtime

The compiler (`llvmnet`) and `LlvmNet.Runtime` share one version, set by `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`. The .NET SDK appends the git commit to the informational version, so a development build reports `0.3.0-dev+<commit>` and a release build reports `0.3.0+<commit>`.

- `llvmnet --version` prints that version, the LLVM version loaded into the compiler, and the ABI tags it can emit.
- Every emitted assembly carries three `AssemblyMetadata` entries: `llvmnet.abi`, `llvmnet.version` and `llvmnet.llvm`. `LlvmNet.Checks inspect <assembly>` prints them.
- Each emitted image's type initializer calls `AbiContract.Require(abi, compilerVersion)`. It throws before any translated code runs when the loaded runtime is incompatible with the compiler that produced the image.
- `--cil-reference` rejects a managed library compiled by an incompatible llvmnet version at compile time.

Compatibility follows Semantic Versioning with the usual 0.x rule: while the major version is 0, the major and minor versions must both match, because any 0.x minor release may change the runtime interface. From 1.0 on, the major version must match.

## Release procedure

1. Set `VersionSuffix` to empty and `VersionPrefix` to the release version in `Directory.Build.props`.
2. Move the `Unreleased` section of `CHANGELOG.md` under the new version heading.
3. Commit, tag `v<version>`, and let CI build the packages from the tag.
4. Bump `VersionPrefix` to the next minor version and restore `VersionSuffix` to `dev`.

## ABI tags

ABI tags are versioned separately from the compiler. `dotnet64-v1` and `system-linux-x64-v1` stay fixed for the lifetime of their documented contracts. An incompatible change creates a new tag, such as `dotnet64-v2`, and both tags are then rejected when mixed in one program. See [ADR-09](../plan/decisions.md#adr-09-portable-abi-evolution).

## LLVM

Each llvmnet release pins exactly one LLVM release, currently 22.1.8. See [ADR-06](../plan/decisions.md#adr-06-llvm-version-policy).
