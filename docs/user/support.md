# Support and deprecation policy

This page states what a release supports, for how long, and how features are retired. The terms marked *proposed* are recommendations recorded for the owner in [the decision log](../plan/decisions.md#proposed-for-the-owner-2026-11-01-m9) and apply once accepted; the rest restates decisions already taken.

## What a release supports

| Area | 1.x | Source |
| --- | --- | --- |
| Compiler host | Linux x86-64 | ADR-05 |
| Hosts for portable images | Linux x86-64 and ARM64, Windows x64, macOS ARM64, under the JIT and as NativeAOT executables | ADR-05 |
| Hosts for system-mode images | Linux x86-64 | ADR-05 |
| .NET | .NET 10 (LTS); images run on 10.x and its patch releases (`rollForward: Minor`) | ADR-05, XP-06 |
| LLVM | exactly one major per llvmnet minor release, LLVM 22 today; the bundled package carries it | ADR-06 |
| Portable ABI | `dotnet64-v2`, frozen at 1.0: headers, host import table, varargs layout, `long double`, struct layouts | ADR-09, RL-02 |
| System ABI | `system-linux-x64-v1` | ADR-09 |
| Managed library surface | scalar and pointer `CExport` methods of `--emit-library`; aggregate signatures are not stable | ADR-09 |

[deployment.md](deployment.md) lists what to install on each host; [limitations.md](limitations.md) what does not work on any of them.

## Compatibility between versions

From 1.0, the major version of the compiler and of `LlvmNet.Runtime` must match: an image refuses to start with a runtime of another major version ([dev/versioning.md](../dev/versioning.md)). Within a major version:

- Objects and archives compiled for an ABI tag link with any later 1.x compiler that emits the same tag. A new tag (`dotnet64-v3`) is introduced only together with a new major version, and the linker rejects mixed tags.
- A 1.x minor release may move to a newer LLVM major (ADR-06). *Proposed:* objects compiled by an earlier 1.x release stay valid link inputs, since LLVM reads bitcode written by older releases; a change that cannot keep this needs a new major version.
- *Proposed:* a 1.x minor release may add a newer .NET LTS as a supported runtime but does not drop .NET 10; dropping a .NET version needs a new major version.

## How long a release is supported

*Proposed:*

- The latest 1.x minor release receives bug and security fixes as patch releases.
- When a new minor release ships, the previous one receives security fixes for three more months.
- When 2.0 ships, the last 1.x minor release receives security fixes for twelve more months, or until .NET 10 reaches its end of support (November 2028), whichever comes first.
- 0.x releases are previews and are not supported once a later 0.x or 1.0 release exists.

## Deprecation

*Proposed:*

1. A feature, option or behavior to be removed is first deprecated in a minor release: the `CHANGELOG.md` entry says so and names the replacement, and the driver prints a warning where the feature is used (for an option, in the same form as its other diagnostics).
2. It stays available, with the warning, for at least one more minor release and at least three months.
3. It is removed only in a major release, never in a patch release.

Two kinds of change are not deprecations and can happen in any release: a fix that makes llvmnet reject a program it compiled wrongly (the rejection names the cause), and a change to an option or behavior documented as experimental or unstable, such as aggregate `CExport` signatures.

Removed before 1.0, without a deprecation period, because 0.x releases are previews: the `managed-host` mode (TC-10), `--native-import` (SA-01) and the `dotnet64-v1` tag (ADR-09).
