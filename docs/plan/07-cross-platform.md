# Workstream XP: Cross-platform execution

Prefix `XP`. Owner: unassigned. Milestone: [M6](README.md#m6-cross-platform).

## Current state (verified 2026-10-30)

- The `dotnet64-v1` ABI is defined for any little-endian 64-bit .NET host, but portable images have only ever executed on Linux x86-64. `AbiContract.Require` rejects 32-bit and big-endian runtimes at startup.
- The compiler itself runs only on Linux: the LLVM bridge is built by a Bash script, `libLLVM-22.so` is hard-coded, launchers are Unix shell scripts, and the system ABI is Linux x86-64 by design.
- NativeAOT publishing for other runtime identifiers has never been executed here.
- The current hand-written runtime contains host assumptions (Linux `open` flag values, `PosixSignal` enumeration casts) that disappear with the musl host layer, where host differences are confined to about 40 functions.

## Items

- [ ] **XP-01** (M, P0) Windows x64 under JIT: path translation in the host layer (`/` separators, drive letters, `realpath`), binary-only I/O (portable mode never translates line endings; documented), UTF-8 console, Ctrl-C as SIGINT, case-insensitive filesystem documented, `mmap` through `MemoryMappedFile`, no `fork`, timezone from `TimeZoneInfo`. A `tests/xp/` tag runs on a Windows runner.
- [ ] **XP-02** (M, P0) Linux ARM64 and macOS ARM64 under JIT: alignment (BE-15), saturating float conversions (BE-12), `Vector128` software fallbacks, atomics (BE-16), long double software paths; the `core` portable tag runs on ARM64 runners.
- [ ] **XP-03** (L, P0) NativeAOT on `win-x64`, `osx-arm64` and `linux-arm64`: CI publishes and runs the portable C, C++ exception and thread fixtures; native toolchain requirements per platform are documented in TC-12.
- [ ] **XP-04** (L, P1) Compiler on Windows and macOS hosts: CMake build for the LLVM bridge (FD-09), per-OS LLVM library names, process spawning and quoting, temp directories, `.cmd` launchers, per-host packaging, and sourcing LLVM 22 binaries from official releases. If this slips, 1.0 ships the compiler for Linux only with outputs running everywhere ([ADR-05](decisions.md#adr-05-supported-platforms-for-10)).
- [ ] **XP-05** (M, P1) Cross-publishing NativeAOT from Linux to `linux-arm64` with a cross toolchain, documented.
- [ ] **XP-06** (S, P0) Actionable startup errors on unsupported hosts (32-bit, big-endian, Mono, old runtimes) and a `llvmnet.runtimeconfig.json` that pins the framework band. — **In progress** (`ad3d6b2`): The runtime rejects incompatible compiler versions; host checks are unchanged.
- [ ] **XP-07** (M, P1) Portability audit of the runtime: no `Vector<T>`, no `IntPtr.Size` assumptions beyond the 64-bit check, no OS branching outside the host layer, no reliance on `Console` encodings.
- [ ] **XP-08** (S, P1) Windows and macOS system-ABI modes are out of scope for 1.0; documented.

## Exit criteria for M6

XP-01, XP-02, XP-03 and XP-06 are done and green in CI for two consecutive weeks.
