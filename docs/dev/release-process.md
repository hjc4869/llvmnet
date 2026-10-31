# Release process

A release is a tag, a `CHANGELOG.md` section, the artifacts the tag's workflow builds and signs, and an updated status in the plan. Versions follow [versioning.md](versioning.md): SemVer for the compiler, with the ABI tags versioned separately.

## Cutting a release

1. Make sure the gate is green on `master` and the nightly jobs have been green for the period the milestone's exit criterion names ([plan/README.md](../plan/README.md)).
2. In `Directory.Build.props`, set `VersionPrefix` to the release version and `VersionSuffix` to empty.
3. Move the `Unreleased` section of `CHANGELOG.md` under a `## [<version>] - <date>` heading; the release notes are taken from it.
4. Commit, tag `v<version>` and push the tag. The `release` workflow (`.github/workflows/release.yml`) builds the toolchain in the CI container, runs the unit tests and the core and toolchain tags (the full package and deployment test included), builds the artifacts twice and checks that both builds give the same SHA-256 sums, signs every file, and creates the GitHub release with the artifacts and the notes attached.
5. Bump `VersionPrefix` to the next minor version and restore `VersionSuffix` to `dev`.

## The artifacts

`scripts/release-artifacts.sh <out-dir> [version]` builds, for a commit:

| File | Contents |
| --- | --- |
| `llvmnet-<version>-linux-x64.tar.xz` | The thin toolchain: the compiler with its own .NET runtime (`--self-contained`), the LLVM bridge, the sysroots, the documentation, the licenses; LLVM 22 is expected on `PATH` |
| `llvmnet-<version>-linux-x64-llvm.tar.xz` | The same with Clang, Flang, `opt`, the archive tools, `libLLVM` and their libraries, the builtin headers and the Flang modules bundled (`--bundle-llvm`) |
| `llvmnet.<version>.nupkg` | The dotnet tool: `dotnet tool install -g llvmnet --add-source <dir>` installs the `llvmnet` command with the bridge and the sysroots; LLVM 22 on `PATH` |
| `SHA256SUMS` | The sums of the files above |
| `<file>.sigstore.json` | A Sigstore bundle per file, made by the workflow with cosign's keyless signing through GitHub's OIDC token |

The artifacts are reproducible: `tar` sorts the names, fixes the owner to 0 and every timestamp to the commit's (`SOURCE_DATE_EPOCH`), and drops the atime and ctime headers; `xz` uses a fixed block size so the thread count does not change the stream; the .NET build is deterministic and the version string comes from the commit; the tool package is rewritten with the commit time in its zip entries, because NuGet stamps them with the build time. `tests/release.sh` builds the artifacts twice and compares the sums; the workflow does the same before signing. A release is built from a committed tree: the package copies `docs/`, so an uncommitted edit there changes the archive.

## Verifying a download

```sh
sha256sum -c SHA256SUMS
cosign verify-blob --bundle llvmnet-0.3.0-linux-x64.tar.xz.sigstore.json \
  --certificate-identity-regexp '^https://github.com/<owner>/llvmnet/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com llvmnet-0.3.0-linux-x64.tar.xz
```

The certificate identity is the workflow that built the release (`.github/workflows/release.yml` at the tag), so a signature proves the file came out of that workflow on that repository, with no long-lived key to protect or rotate.

## Publishing the tool to nuget.org

The workflow attaches the `.nupkg` to the release and does not push it to nuget.org, which needs an API key held by the owner: `dotnet nuget push llvmnet.<version>.nupkg --source https://api.nuget.org/v3/index.json --api-key <key>`.
