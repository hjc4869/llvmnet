# llvmnet production readiness plan

This directory is the working plan for taking llvmnet from a prototype that runs ffmpeg and SPEC CPU to a toolchain that other people can depend on. It is written for human review and progress tracking: every piece of work is a checkbox with an ID, a size, a priority and a done condition, grouped into workstreams and sequenced by milestones. The architecture decisions the plan depends on are listed separately so they can be accepted or rejected before work starts.

Related: [roadmap.md](../roadmap.md) (the earlier priority ordering), [status.md](../status.md), [architecture.md](../architecture.md), [toolchain.md](../toolchain.md).

## How to use this plan

- **Items** live in the workstream files below. Each looks like `- [ ] **<prefix>-<number>** (size, priority) ...` with a done condition. Tick the box and append the PR or commit when done. Add `— blocked: <reason>` or `— descoped: <ADR>` rather than deleting an item.
- **Sizes**: S about a day, M about a week, L several weeks, XL needs its own breakdown before it starts. **Priorities**: P0 blocks 1.0, P1 should ship in 1.0, P2 is a post-1.0 candidate.
- **Milestones** (M0 to M9) have exit criteria that name the items they need. A milestone is done when its exit criteria hold on CI, not when the items are ticked.
- **Decisions** (ADR-01 to ADR-13 in [decisions.md](decisions.md)) are Proposed until the project owner marks them Accepted. Items that depend on a decision say so.
- **Review cadence**: once a week, update the status board below, tick items, and add new items with the next free number in their workstream. Never renumber. If an issue tracker is used, one issue per item with the ID in the title and a label per workstream and milestone.
- **Evidence**: a claim that something works belongs in `docs/evidence/` (dated, produced by CI) or in a test, not in this plan.

## Progress (2026-10-30, M0 to M2)

Work on the `m0-m2` branch completed the M1 and M2 exit items and every M0 item except running the PR gate on GitHub. Measurements are in [evidence/2026-10-30-m0-m2.md](../evidence/2026-10-30-m0-m2.md).

- **M0 foundations.** License (Apache-2.0 WITH LLVM-exception), notices, versioning, pinned sources, a parallel test runner (77 tests, about 3 minutes with eight jobs), 61 unit tests, JIT validation of every emitted method, and a CI workflow whose PR gate passes in the matching container. Not yet: a first run on GitHub (FD-19) and fuzzing (FD-16).
- **M1 backend hardening.** 44,762 IR semantics probes match native bit for bit; linkage, termination order, determinism, atomics, alignment, call-signature policy, vectorization and diagnostics are done; the support matrix is generated. Six behavior bugs and several missing features were found and fixed (listed in the evidence file). The fuzzing criterion is open because FD-16 has not started.
- **M2 library spike.** Unmodified musl 1.2.5, upstream libc++/libc++abi and flang-rt 22.1.8 compile completely for dotnet64-v1 and match native output under JIT and NativeAOT, through `--portable-libc` and a host layer of four entry points. ADR-01, 02, 03 and 07 are accepted. New decisions needed before M3: the ABI tag of the musl sysroot (LC-26, ADR-09) and the Linux identity of portable programs (LC-27, ADR-14).

## Status snapshot (2026-10-30, before M0)

Verified on this host during the plan review (Linux x86-64, .NET SDK 10.0.403, runtime 10.0.14, LLVM/Clang/Flang 22.1.8):

| Check | Result |
| --- | --- |
| `dotnet build llvmnet.slnx -c Release` | succeeded, 0 warnings |
| `tests/smoke.sh`, `tests/driver.sh` | pass (2 s and 8 s) |
| `tests/all.sh core` (53 scripts, JIT and NativeAOT, both ABIs) | pass, 151 checks, 376 s wall (sequential runner before FD-12) |
| Integration, ffmpeg, SPEC tags | not rerun today; last evidence 2026-09-08 in [status.md](../status.md) |

What exists and works, per the September evidence: C, C++ and Fortran compile through Clang/Flang 22 to CIL; three runtime modes (`managed-host`, `system`, `portable`); NativeAOT output; a relocatable toolchain package; the ffmpeg browser profile with threading and SIMD128 kernels matching native hashes on a 4K HDR file; every installed SPEC CPU 2017/2026 entry validated on test inputs in system mode, 20 of 101 in portable mode.

Size of the code base: 372 tracked files (2.1 MB); about 10,400 lines of C# (3,500 compiler, 6,900 runtime), 271 lines of C++ bridge, 798 lines of hand-written portable headers, 2,200 lines of Bash tests and 4,100 lines of test fixtures. The runtime exports 538 C-callable entry points (275 libc, 31 pthread, 60 C++ ABI, 126 Fortran, 46 SIMD128).

Prototype-grade areas, in order of impact:

1. **The portable C library** is hand-written C# with no conformance baseline, thread-safety and buffering defects, magic errno/flag constants, and large gaps (no `locale.h`, `wctype.h`, `fenv.h`, `complex.h`, `sys/mman.h`, sockets, `regex.h`, and more). Details in [02-portable-c-library.md](02-portable-c-library.md).
2. **The C++ and Fortran runtimes** are partial hand-written implementations of the libc++ ABI and the Flang runtime; iostreams, locale, final procedures, defined I/O and most reductions are missing. Details in [03-cxx-runtime.md](03-cxx-runtime.md) and [04-fortran-runtime.md](04-fortran-runtime.md).
3. **Engineering infrastructure**: no license, no CI, no unit tests, sequential Bash gates that stop at the first failure, machine-specific paths in integration scripts, hard-coded version string. Details in [00-foundations.md](00-foundations.md).
4. **The backend** is solid where tested but monolithic (one 1,500-line file), has a process-wide atomics lock, no debug information, no diagnostics with source locations, and no support matrix. Details in [01-compiler-backend.md](01-compiler-backend.md).
5. **Cross-platform execution** of portable images is designed but has never been exercised outside Linux x86-64. Details in [07-cross-platform.md](07-cross-platform.md).

## Goal and definition of production-ready

**Goal.** A compiler toolchain that turns C, C++ and Fortran sources into .NET assemblies which run unchanged on every supported .NET host (portable mode) or interoperate with the native Linux ABI (system mode), with behavior that matches native Clang/Flang builds or fails loudly at compile time.

**Production-ready (1.0) means all of the following are true and checked by CI:**

| # | Criterion | Measured by |
| --- | --- | --- |
| R1 | Correctness: a program accepted by llvmnet behaves as its native build does, or compilation fails with a diagnostic. No silent miscompiles, no stubbed-out operations. | WG-01/02 conformance suites at or above 99%, fuzzing (FD-16) with no open miscompile, library suites (WG-03) |
| R2 | Complete standard libraries: C (musl), C++ (libc++), Fortran (flang-rt) with their upstream test suites passing in portable mode, except a documented ENOSYS set. | LC-19, CX-08, FT-06 |
| R3 | Real workloads: ffmpeg FATE subset and SPEC CPU 2017/2026 test and train inputs pass in both modes under JIT and NativeAOT. | WG-04, WG-06 |
| R4 | Platforms: the same portable image runs on Linux x64/ARM64, Windows x64 and macOS ARM64, JIT and NativeAOT. | XP-01..03 in CI |
| R5 | Diagnosable: compiler errors carry file and line; runtime faults report the C call stack; `-g` yields source lines in stack traces. | BE-04, BE-28, BE-29 |
| R6 | Distributable: signed, reproducible, versioned packages with a license and third-party notices; documented driver, ABI and limitations. | TC-11, TC-16, FD-01/02/05 |
| R7 | Performance: published numbers against native on a fixed benchmark set, meeting the agreed target or explaining the gap. | PF-01, ADR-08 |
| R8 | Maintainable: PR gate under 30 minutes, unit tests, no file over about 600 lines in the compiler, support matrix generated from code, a contribution guide. | FD-12..15, BE-01/02 |

## Strategy

Three bets shape the sequencing:

1. **Replace, do not extend, the hand-written libraries.** Compile upstream musl, libc++/libc++abi and flang-rt to portable bitcode with llvmnet itself, leaving a small versioned host layer in C#. This converts an open-ended reimplementation into a porting task with upstream test suites as the finish line. It is the same architecture emscripten and wasi-libc use. [ADR-01](decisions.md#adr-01-portable-c-library-strategy), [ADR-02](decisions.md#adr-02-c-runtime-strategy), [ADR-03](decisions.md#adr-03-fortran-runtime-strategy).
2. **Make conformance suites the definition of done.** `llvm-test-suite`, GCC torture, musl `libc-test`, libc++ lit, FCVS and ffmpeg FATE replace "the workload we tried last passed".
3. **Build the infrastructure before the big rewrite.** License, CI, parallel test runner, unit tests and IL verification come first so the library replacement is reviewable in small steps.

Non-goals for 1.0: native C++ ABI interoperability, general SIMD lowering, sockets in portable mode, system-ABI modes on non-Linux hosts, a sandbox or memory-safety guarantee for untrusted code.

## Milestones

| Milestone | Purpose | Depends on | Rough effort (engineer-weeks) |
| --- | --- | --- | --- |
| [M0](#m0-foundations) Foundations | License, CI, test runner, unit tests, reproducible inputs | | 3 to 4 |
| [M1](#m1-backend-hardening) Backend hardening | Semantics audits, diagnostics, atomics, linkage, limits | M0 | 4 to 6 |
| [M2](#m2-library-architecture-spike) Library architecture spike | Prove musl, libc++, flang-rt build and run; accept ADR-01..03, 07 | M1 (partly) | 4 to 6 |
| [M3](#m3-portable-c-library) Portable C library | musl + host layer; delete hand-written libc and `managed-host` | M2 | 6 to 10 |
| [M4](#m4-portable-c-runtime) Portable C++ runtime | libc++/libc++abi delivery | M3 | 4 to 6 |
| [M5](#m5-fortran-runtime) Fortran runtime | flang-rt delivery, portable OpenMP | M4 | 4 to 6 |
| [M6](#m6-cross-platform) Cross-platform | Windows/macOS/ARM64 JIT and NativeAOT in CI | M3 | 4 to 6 |
| [M7](#m7-toolchain-and-distribution) Toolchain and distribution | Driver table, packaging, docs, system-ABI hardening | M3 | 4 to 6 |
| [M8](#m8-performance) Performance | Benchmarks, atomics, allocas, method splitting, AOT policy | M3 | 4 to 8 |
| [M9](#m9-workload-gates-and-10) Workload gates and 1.0 | Full suites on the release candidate | all | 4 to 6 |

The effort column is a rough order of magnitude for one experienced engineer and will be wrong in detail; it exists so milestones can be compared and so the total (roughly 45 to 65 engineer-weeks) is visible. M6, M7 and M8 can run in parallel with M4 and M5 once M3 is done.

### Status board

Update weekly. "Open P0" counts unchecked P0 items that the milestone's exit criteria name.

| Milestone | Target version | Status | Open P0 | Last updated |
| --- | --- | --- | ---: | --- |
| M0 | 0.3 | exit items done except the first GitHub run of the PR gate | 1 (FD-19) | 2026-10-30 |
| M1 | 0.3 | exit items done; two-week fuzzing criterion open (FD-16) | 0 | 2026-10-30 |
| M2 | 0.3 | done; ADR-01, 02, 03, 07 accepted | 0 | 2026-10-30 |
| M3 | 0.4 | spike host layer exists; LC-10 done | 16 (incl. LC-26) | 2026-10-30 |
| M4 | 0.5 | not started | 7 | 2026-10-30 |
| M5 | 0.5 | not started | 6 | 2026-10-30 |
| M6 | 0.6 | not started | 4 | 2026-10-30 |
| M7 | 0.7 | not started | 11 | 2026-10-30 |
| M8 | 0.7 | not started | 2 | 2026-10-30 |
| M9 | 1.0-rc | not started | 6 | 2026-10-30 |

### M0: Foundations

Exit: FD-01, FD-02, FD-05, FD-06, FD-09, FD-12, FD-13, FD-14, FD-15, FD-19 done; PR gate green on three consecutive merges. Details: [00-foundations.md](00-foundations.md). Decisions: ADR-13 (license).

### M1: Backend hardening

Exit: BE-04, BE-05, BE-07, BE-08, BE-11, BE-12, BE-15, BE-16, BE-18, BE-25, BE-31 done; two weeks of fuzzing without an open miscompile; support matrix published. Details: [01-compiler-backend.md](01-compiler-backend.md). Decisions: ADR-06.

### M2: Library architecture spike

Exit: LC-01, LC-02, CX-01, FT-01 executed with written results; LC-03 recorded; ADR-01, ADR-02, ADR-03 and ADR-07 Accepted or replaced; every backend blocker found has a BE item. This milestone exists to fail early if the strategy is wrong. Details: [02-portable-c-library.md](02-portable-c-library.md), [03-cxx-runtime.md](03-cxx-runtime.md), [04-fortran-runtime.md](04-fortran-runtime.md).

### M3: Portable C library

Exit: LC-04 through LC-10, LC-14 through LC-17, LC-19 through LC-23 and LC-26 done; portable ffmpeg profile, 31 fixtures and the 4K HDR gate pass; SPEC portable C entries at parity with system mode; `managed-host` removed. Details: [02-portable-c-library.md](02-portable-c-library.md). Decisions: ADR-04.

### M4: Portable C++ runtime

Exit: CX-02 through CX-08 done; libc++ lit directories at or above 98%; SPEC C++ portable parity. Details: [03-cxx-runtime.md](03-cxx-runtime.md).

### M5: Fortran runtime

Exit: FT-02 through FT-07 done; FCVS and `llvm-test-suite/Fortran` at or above 98%; SPEC Fortran portable parity. Details: [04-fortran-runtime.md](04-fortran-runtime.md).

### M6: Cross-platform

Exit: XP-01, XP-02, XP-03, XP-06 done and green for two consecutive weeks. Details: [07-cross-platform.md](07-cross-platform.md). Decisions: ADR-05.

### M7: Toolchain and distribution

Exit: TC-01 through TC-05, TC-11 through TC-13, TC-16, TC-17 done; SA-01 decided; signed packages produced by CI from a tag. Details: [06-driver-and-toolchain.md](06-driver-and-toolchain.md), [05-system-abi.md](05-system-abi.md). Decisions: ADR-09, ADR-11, ADR-12.

### M8: Performance

Exit: PF-01 and PF-02 done; nightly benchmark table published; ADR-08 target met or the gap explained with numbers. Details: [08-performance.md](08-performance.md).

### M9: Workload gates and 1.0

Exit: WG-01 through WG-06 at threshold on the release candidate for two consecutive nightlies; every RL item ticked. Details: [09-workload-gates.md](09-workload-gates.md), [10-release.md](10-release.md).

## Workstreams

| Prefix | File | Scope |
| --- | --- | --- |
| FD | [00-foundations.md](00-foundations.md) | License, build, test runner, unit tests, CI |
| BE | [01-compiler-backend.md](01-compiler-backend.md) | IR-to-CIL correctness, structure, diagnostics, limits, debug info |
| LC | [02-portable-c-library.md](02-portable-c-library.md) | musl port, host layer, libc conformance |
| CX | [03-cxx-runtime.md](03-cxx-runtime.md) | libc++/libc++abi, exception bridge, RTTI |
| FT | [04-fortran-runtime.md](04-fortran-runtime.md) | flang-rt, Fortran conformance, portable OpenMP |
| SA | [05-system-abi.md](05-system-abi.md) | Native bridge hardening, callbacks, TLS, signals |
| TC | [06-driver-and-toolchain.md](06-driver-and-toolchain.md) | Driver options, outputs, packaging, documentation |
| XP | [07-cross-platform.md](07-cross-platform.md) | Windows, macOS, ARM64, NativeAOT matrix |
| PF | [08-performance.md](08-performance.md) | Benchmarks and known hot spots |
| WG | [09-workload-gates.md](09-workload-gates.md) | Conformance suites, ffmpeg FATE, SPEC |
| RL | [10-release.md](10-release.md) | Pre-release train and the 1.0 checklist |

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| The musl thread/TLS layer cannot be mapped onto managed threads cleanly | M3 slips; pthreads stay hand-written | LC-02 is a spike with a named fallback (keep C# pthreads, exclude musl `src/thread`); picolibc is the libc fallback |
| Very large functions (SPEC gcc, perlbench, wrf) hit JIT MinOpts or CLR local limits | Performance and, at the extreme, compile failures | BE-25 measures first; BE-26 splitting; documented limits in the meantime |
| Debug information turns out to be large | R5 partially missed | ADR-12 scopes 1.0 to sequence points only |
| Cross-platform NativeAOT CI is expensive and flaky | M6 slips | Start with JIT on all platforms (XP-01/02), AOT nightly only |
| LLVM major version churn (new release every six months) | Upgrade cost every cycle | ADR-06 pins one major per release; the IR corpus is the regression net |
| Licensed SPEC kits cannot run in public CI | Release gate depends on a private runner | FD-21 documents the runner; WG-06 publishes summaries only |
| Single maintainer | Throughput and bus factor | M0 makes the project contributable; the plan itself is the hand-over document |
| Performance gap versus native discourages adoption | R7 missed | ADR-08 sets an explicit target; PF-01 publishes numbers early rather than late |

## Suggested first four weeks

1. Accept or amend ADR-13 (license) and ADR-01/02/03/07 as working hypotheses so the spike can start.
2. FD-01, FD-02, FD-05 (one day each), then FD-12/FD-13 (runner, build once) and FD-19 (PR gate).
3. In parallel, BE-07 (linkage), BE-16 (atomics) and BE-04 (diagnostics), because the musl spike will hit all three.
4. Start LC-01 as soon as BE-07 lands; report blockers as BE items weekly.
