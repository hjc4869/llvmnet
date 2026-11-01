# Workstream SA: System ABI and native interoperability

Prefix `SA`. Owner: unassigned. Milestone: [M7](README.md#m7-toolchain-and-distribution) for P0/P1 items; the mode is already functional.

## Current state (verified 2026-10-30)

- `--runtime=system` (`system-linux-x64-v1`) links translated code against the real glibc, libm and user libraries through LLVM-generated per-signature native bridges (`HostInterop.cs`, `native/abi.cpp`), unmanaged-callable thunks for C callbacks, native pthreads with managed-entry/TLS adapters, and a static native input for NativeAOT. All installed SPEC entries validated in this mode.
- Explicit limits: native code cannot invoke translated variadic or aggregate callbacks; external native TLS globals need a bitcode definition; foreign-thread TLS teardown, `pthread_exit`/cancellation and hardware-fault signal recovery are incomplete; native C++ ABI interchange is a non-goal. The older `--native-import` scalar P/Invoke path was removed on 2026-10-31 (SA-01); `--system-library` is the one native path.

## Items

- [x] **SA-01** (S, P0) Decide the fate of `--native-import` (legacy scalar P/Invoke): fold its use cases into `--system-library` or keep it as a documented escape hatch; remove dead paths accordingly ([ADR-04](decisions.md#adr-04-retire-managed-host-mode)). — **Decided** (2026-10-31, ADR-04): remove it; the option fails with guidance pointing at `--system-library`. — **Done** (2026-10-31): the P/Invoke emission, the import table and the two fixtures are deleted; `--native-import` fails with "was removed (ADR-04, SA-01)" and the guidance (`tests/modes.sh`, `DriverTests`); evidence in [evidence/2026-10-31-m7-toolchain.md](../evidence/2026-10-31-m7-toolchain.md#sa-01---native-import-removed).
- [ ] **SA-02** (M, P1) Native invocation of translated aggregate and variadic callbacks: generate LLVM thunks that unpack native registers into the software argument record (the inverse of the call bridge). Motivating cases: `qsort` with struct elements through native libc, libxml2 variadic error callbacks, OpenMP outlined functions with struct arguments.
- [ ] **SA-03** (M, P1) External native TLS globals resolved through generated per-thread accessors (`dlsym` plus `__tls_get_addr`), or a link-time diagnostic naming the symbol.
- [ ] **SA-04** (M, P1) Foreign threads (created by native libraries) calling translated callbacks: lazy runtime attach, TLS initialization, destructor ordering; test with a native pthread pool calling back into translated code under JIT and NativeAOT.
- [ ] **SA-05** (M, P1) `pthread_exit`, cancellation points and `pthread_cleanup_push` across managed frames: implement what the CLR permits and reject the rest at link time where detectable.
- [ ] **SA-06** (M, P1) Signals in system mode: real `sigaction` handlers with a trampoline into translated code and the documented restrictions; memory-fault recovery documented as impossible.
- [ ] **SA-07** (M, P1) Deployment: decide whether the JIT sidecar `.llvmnet-host.so` is embedded as a resource and extracted at startup or required alongside the image; document RPATH and `--system-library` copying; NativeAOT keeps static linking. — **Decided** (2026-10-31): the sidecar ships next to the image in the payload folder and is found by a fixed relative path, with no embedding or extraction. Documentation open.
- [ ] **SA-08** (S, P1) ABI tag `system-linux-x64-v1` stays; a Linux ARM64 system ABI requires a bridge port and is post-1.0 ([XP](07-cross-platform.md)).
- [ ] **SA-09** (M, P1) Bridge fuzzing: random C signatures (struct sizes 1 to 64 bytes, mixed float/integer members, varargs with long double) compiled natively and through the bridge, comparing results; stored as a nightly job.
