# Workstream RL: Release management

Prefix `RL`. Owner: unassigned. Covers the pre-release train and the 1.0 checklist.

## Pre-release train

Each pre-release is a tag, a CHANGELOG section, a package built by CI, and an updated status snapshot in [README.md](README.md). Versions are proposals; the sequence matters more than the numbers.

| Version | Contents | Gate |
| --- | --- | --- |
| 0.3 | M0 foundations, M1 backend hardening, M2 spike results and recorded ADRs | PR gate green; core tag under FD-12; ADR-01..03 accepted |
| 0.4 | M3 musl-based portable libc; `managed-host` removed; hand-written libc deleted | musl `libc-test`, ffmpeg portable, SPEC portable parity for C |
| 0.5 | M4 libc++/libc++abi and M5 flang-rt | libc++ lit, FCVS, SPEC C++/Fortran portable parity |
| 0.6 | M6 cross-platform JIT and NativeAOT on the supported matrix | `tests/xp` green on all runners |
| 0.7 | M7 driver, packaging, docs; M8 performance | signed packages; performance target met or formally deferred |
| 1.0-rc | M9 workload gates on the release candidate | two consecutive green nightlies |
| 1.0 | | all RL items below |

## 1.0 checklist

- [ ] **RL-01** Every P0 item in workstreams FD, BE, LC, CX, FT, TC, XP, WG is done or formally descoped with a note in the ADR log.
- [ ] **RL-02** `dotnet64-v1` is frozen: the header set, host import table, varargs layout, `long double`, descriptor layout and errno table are documented in `docs/user/abi.md`, and a compatibility test compiles a 0.4-era program against the 1.0 sysroot.
- [ ] **RL-03** Supported-platform matrix ([ADR-05](decisions.md#adr-05-supported-platforms-for-10)) is green in CI for the release candidate build.
- [ ] **RL-04** Conformance thresholds ([WG](09-workload-gates.md)) met on the release candidate.
- [ ] **RL-05** Performance target ([ADR-08](decisions.md#adr-08-performance-target)) met or formally deferred with numbers.
- [ ] **RL-06** User documentation complete ([TC-16](06-driver-and-toolchain.md)); limitations page generated ([TC-17](06-driver-and-toolchain.md)); examples build in CI ([TC-18](06-driver-and-toolchain.md)).
- [ ] **RL-07** Packages signed, checksummed, reproducible from the tag ([TC-11](06-driver-and-toolchain.md)); third-party notices complete ([FD-02](00-foundations.md)).
- [ ] **RL-08** `SECURITY.md` (what is and is not a sandbox, how to report), support policy (LLVM major band, .NET LTS band, length of support for 1.x), deprecation policy.
- [ ] **RL-09** Deprecated paths removed: `managed-host`, `--native-import` if ADR-04 says so, hand-written runtime files, legacy test scripts.
- [ ] **RL-10** CHANGELOG finalized; tag; release notes; status snapshot in this plan updated for the last time before 1.x maintenance planning begins.
