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
| 0.7 | M7 driver, packaging, docs; M8 performance | signed packages; performance numbers published |
| 1.0-rc | M9 workload gates on the release candidate | two consecutive green nightlies |
| 1.0 | | all RL items below |

## 1.0 checklist

- [ ] **RL-01** Every P0 item in workstreams FD, BE, LC, CX, FT, TC, XP, WG is done or formally descoped with a note in the ADR log.
- [x] **RL-02** `dotnet64-v2` is frozen ([ADR-09](decisions.md#adr-09-portable-abi-evolution)): the header set, host import table, varargs layout, `long double`, descriptor layout and errno table are documented in `docs/user/abi.md`, and a compatibility test compiles a 0.4-era program against the 1.0 sysroot. — **Done** (2026-11-01): [docs/user/abi.md](../user/abi.md) states the frozen contract (header set by its stamp, target and data layout, scalar types, `long double`, the SysV `va_list`, musl structure layouts, signal and errno numbering, C++ and Fortran ABIs, the host interface, the object tag). `tests/abi-freeze.sh` (core gate) fails when the header stamp or the 98 observable layout facts of `tests/abi/layout.c` change, and links `tests/abi/compat-object-v2.o`, bitcode compiled at the freeze, with a freshly compiled program under the JIT and as NativeAOT, comparing its output with the native build's: the 0.4-era program of the item is that object, which every later toolchain must keep linking.
- [ ] **RL-03** Supported-platform matrix ([ADR-05](decisions.md#adr-05-supported-platforms-for-10)) is green in CI for the release candidate build.
- [ ] **RL-04** Conformance thresholds ([WG](09-workload-gates.md)) met on the release candidate.
- [ ] **RL-05** Performance numbers against native published ([PF-01](08-performance.md)); [ADR-08](decisions.md#adr-08-performance-target) defers a numeric target past 1.0.
- [ ] **RL-06** User documentation complete ([TC-16](06-driver-and-toolchain.md)); limitations page generated ([TC-17](06-driver-and-toolchain.md)); examples build in CI ([TC-18](06-driver-and-toolchain.md)).
- [ ] **RL-07** Packages signed, checksummed, reproducible from the tag ([TC-11](06-driver-and-toolchain.md)); third-party notices complete ([FD-02](00-foundations.md)).
- [x] **RL-08** `SECURITY.md` (what is and is not a sandbox, how to report), support policy (LLVM major band, .NET LTS band, length of support for 1.x), deprecation policy. — **Done** (2026-11-01): `SECURITY.md` says what llvmnet does not protect against (not a sandbox, no checks the source lacks), what counts as a vulnerability and how to report one; [docs/user/support.md](../user/support.md) states the supported hosts, .NET and LLVM bands and ABI compatibility, the length of support and the deprecation policy. The support terms and the reporting channel are [proposed for the owner](decisions.md#proposed-for-the-owner-2026-11-01-m9).
- [x] **RL-09** Deprecated paths removed: `managed-host`, `--native-import` (ADR-04, SA-01), hand-written runtime files, legacy test scripts. — **Done** (2026-11-01): `managed-host` (TC-10, ADR-04) and `--native-import` (SA-01) were removed before; the hand-written C library was deleted by LC-17; the last legacy test scripts, the unreferenced `tests/all.sh` and `tests/toolchain-all.sh` wrappers, are removed (`tests/run.sh` selects tests by tag).
- [ ] **RL-10** CHANGELOG finalized; tag; release notes; status snapshot in this plan updated for the last time before 1.x maintenance planning begins.
