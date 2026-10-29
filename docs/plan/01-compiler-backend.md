# Workstream BE: Compiler backend (LLVM IR to CIL)

Prefix `BE`. Owner: unassigned. Primary milestone: [M1](README.md#m1-backend-hardening); several items are prerequisites for the library spikes in M2.

The backend is the part of llvmnet that must be kept. This workstream makes it provably correct for the IR that real libraries (musl, libc++, flang-rt, ffmpeg, SPEC) produce, makes failures diagnosable, and removes the structural obstacles to maintaining it.

## Current state (verified 2026-10-30)

- About 3,500 lines of C# in `src/LlvmNet.Compiler` across 12 files. `FunctionEmitter.cs` is 1,516 lines; `Instruction` (about 180 lines), `Call` (about 200) and `Intrinsic` (about 445) are single methods with large switches. `ValueEmitter` owns loads/stores/casts/GEP, `CilCompiler` drives module emission with `PersistedAssemblyBuilder` and patches `calli` signature tokens into the finished PE image.
- LLVM is reached through `DllImport("libLLVM-22.so")` plus a 271-line C++ bridge (`native/*.cpp`, built as `libllvmnet-llvm.so`) for linking, ABI stamping, native shims, vector-invoke wrapping and exact constant extraction. The LLVM major version is hard-wired in library names and `-22` tool suffixes.
- Legalization runs as `opt-22` passes (`internalize`, `globaldce`, `expand-reductions`, `scalarizer`) before emission.
- 29 `llvm.*` intrinsic families are recognized; unrecognized ones raise `NotSupportedException` (134 such throw sites in the compiler and runtime). There is no machine-readable support matrix.
- Atomics are implemented with one process-wide lock (`Atomics.gate`) for every width and address. Correct, but every atomic in a threaded program serializes on it.
- Allocas use `localloc`, or a heap-backed `StackMemory` list when the function has stack save/restore or nonlocal jumps. `byval` arguments are copied per call.
- Debug metadata is discarded (`llvm.dbg.*` ignored, `-g` forwarded to Clang only). Stack traces show `Program.<llvm-name>` and no source lines. Diagnostics are exception messages without file/line; `LLVMNET_TRACE=1` adds a .NET stack trace.
- Known unsupported (from `docs/status.md`): computed goto / `indirectbr`, cross-native-frame `longjmp`, external native TLS, scalable vectors, masked memory intrinsics, `fp128`, some wide-integer intrinsic families, inline assembly beyond an allowlist, and methods that exceed CLR local limits.
- The core gate, driver test and smoke test pass on the current host.

## Items

### Structure and maintainability

- [ ] **BE-01** (L, P1) Split `FunctionEmitter` into cohesive units with no behavior change: instruction lowering, call lowering and signature-compatibility rules, an intrinsic table, exception handling, vectors/SIMD, nonlocal jumps and stack scopes. Done when no compiler file exceeds about 600 lines, intrinsic dispatch is a table of (name pattern, signature check, handler), and the core gate is unchanged commit by commit.
- [ ] **BE-02** (M, P1) Generate the instruction/intrinsic support matrix from code (`llvmnet --print-support` or a test that dumps the table) into `docs/user/limitations.md`, naming the diagnostic each unsupported construct raises.
- [ ] **BE-03** (M, P1) Put all LLVM access behind one binding layer with a startup check of `LLVMGetVersion`, an `LLVMNET_LLVM_LIBRARY` override, a documented upgrade procedure, and no `-22` suffixes outside `Toolchain`. Prepares [ADR-06](decisions.md#adr-06-llvm-version-policy).
- [ ] **BE-04** (M, P0) Diagnostics. Every rejection names the function, prints the offending instruction (`LLVMPrintValueToString`), and uses the `DILocation` when present, in `file:line:col: error:` form. Internal errors print the version, the IR snippet and a bug-report banner; `--keep-ir` is implied on failure with the path reported. Exit codes are documented (1 compile error, 2 usage).
- [ ] **BE-05** (S, P0) Deterministic output. The same bitcode and options produce a byte-identical assembly: content-derived MVID, no timestamps, stable ordering of methods/fields/constants. A CI test compiles twice and compares hashes.
- [ ] **BE-06** (M, P1) Provenance metadata in emitted assemblies (compiler version, LLVM version, options, input hash) readable by `LlvmNet.Checks inspect`.

### Module-level constructs

- [ ] **BE-07** (M, P0) Linkage semantics after linking: global aliases (`@x = alias`), `extern_weak` resolving to null, `weak`/`linkonce_odr`/`comdat` deduplication, `appending` linkage, `llvm.used`/`llvm.compiler.used`, `available_externally`, and an explicit diagnostic for `ifunc`. One hand-written IR test per linkage kind. musl and libc++ depend on weak aliases heavily, so this gates [LC-01](02-portable-c-library.md).
- [ ] **BE-08** (S, P0) `llvm.global_ctors`/`llvm.global_dtors` priority ordering and associated-data handling, interleaving with `atexit` and `__cxa_atexit`, tested with constructors that register destructors.
- [ ] **BE-09** (S, P1) Sections, visibility, `dso_local`, `unnamed_addr`, `no_sanitize`: ignored safely and documented as such.
- [ ] **BE-10** (M, P1) Large initializers: validate the 1 MiB data-field chunking with initializers above 100 MB (SPEC tables), measure compile time and peak memory, and add a size budget warning.

### Instruction semantics

- [ ] **BE-11** (M, P0) Integer audit against LangRef: shifts by amounts at or above the bit width (poison in LLVM; must not trap), `sdiv`/`srem` overflow and division by zero (CIL throws; document as a trap), i128 division/remainder, `trunc`/`zext`/`sext` across 1/8/16/32/64/128 bits, i1 memory representation, pointer comparisons, `ptrtoint`/`inttoptr` widths, `nsw`/`nuw` ignored. Implemented as a generated-IR unit test compared against `lli` or native.
- [ ] **BE-12** (M, P0) Floating-point audit: all 16 `fcmp` predicates with NaN, `fneg`, `fptoui`/`fptosi` out of range (poison in LLVM; .NET 9+ saturates on every platform, so tests must not depend on native x64 behavior), `uitofp` from i64/i128, `frem`, `llvm.copysign`/`fabs`/`floor`/`ceil`/`trunc`/`rint`/`nearbyint`/`round`/`roundeven`/`is.fpclass`, `fma` versus `fmuladd` contraction, denormals and signed zero.
- [ ] **BE-13** (M, P1) `x86_fp80`: complete `Float80` (sqrt, remainder, fma, conversions to and from i128, `nextafter`, rounding modes), measure performance, and add a randomized unit test against the host's native long double. flang-rt's `REAL(10)` and musl's `printf("%Lf")` exercise this path.
- [ ] **BE-14** (L, P2) `fp128` software arithmetic (Fortran `REAL(16)`, `__float128`). Until then reject with a diagnostic; flang-rt is built without quad support ([ADR-03](decisions.md#adr-03-fortran-runtime-strategy)).
- [ ] **BE-15** (M, P0) Memory access: emit `unaligned.` prefixes whenever LLVM alignment is below the natural alignment (required on ARM64 for vectors and atomics), honor `volatile`, handle `llvm.memcpy`/`memmove`/`memset` with volatile flags and i128 lengths, and inline small constant-size copies.
- [ ] **BE-16** (M, P0) Atomics without the global lock: `Volatile`/`Interlocked` on naturally aligned 8/16/32/64-bit addresses, `CompareExchange` loops for read-modify-write, 128-bit CAS through a striped lock, memory orderings mapped to fences, and a diagnostic for misaligned atomics. Tests: existing atomics suite, threaded ffmpeg regression, and a contention benchmark ([PF-02](08-performance.md)).
- [ ] **BE-17** (M, P1) Alloca strategy: static entry-block allocas become one frame allocation; dynamic allocas use a per-thread bump arena with checkpoint/restore instead of per-alloca heap allocations; `main` runs on a thread with a configurable stack (default 8 MiB to match Linux); `pthread_attr_setstacksize` is honored.
- [ ] **BE-18** (M, P0) Indirect-call signature policy. Define and test which call-site/callee signature differences are accepted at `calli` sites (scalar width and sign, pointer versus i64, unused trailing arguments, void versus value) and which are rejected (aggregate layout differences). Direct mismatches get a diagnostic listing both signatures; indirect mismatches go through a checked thunk rather than an `InvalidProgramException`. K&R code, `qsort` comparators and callback tables through `void *` prototypes are the motivating cases.
- [ ] **BE-19** (M, P1) Varargs: complete SysV aggregate classification for the software `va_list` in system mode; evaluate the simpler slot-array layout for portable ABI v2 ([ADR-09](decisions.md#adr-09-portable-abi-evolution)). Tests for struct-by-value, long double and i128 varargs.
- [ ] **BE-20** (M, P1) Control flow: dense `switch` to CIL `switch` tables and sparse to binary search; `indirectbr` (computed goto) lowering through a per-function dispatch block. CPython, perlbench and gcc in SPEC use computed goto.
- [ ] **BE-21** (M, P1) Exception-handling edge cases: `invoke` inside landing pads, cleanup-only pads with `resume`, `catch (...)` with rethrow, `noexcept` termination, and nested exceptions during unwinding. Source the cases from the libc++abi test suite ([CX-08](03-cxx-runtime.md)).
- [ ] **BE-22** (M, P1) `setjmp`/`longjmp`: keep cross-native-frame rejection explicit, measure the exception-based cost on perlbench, and define portable `sigsetjmp` mask behavior (documented no-op versus error).
- [ ] **BE-23** (M, P1) Thread-local globals: cache the per-thread address once per function instead of calling the lazy accessor per use, handle destructor ordering with pthread keys, and either resolve external native TLS in system mode through generated accessors or reject it at link time with the symbol name.
- [ ] **BE-24** (S, P1) Inline assembly policy: keep rejection, document the allowlist (empty barriers, `int3`, CPUID/XGETBV in system mode), point users to no-asm configurations, and confirm musl, libc++, libc++abi and flang-rt build without assembly in the portable configuration.

### CLR limits and scalability

- [ ] **BE-25** (M, P0) Measure the limits real code hits: 65,535 locals, the IL size and basic-block counts beyond which the JIT (also used by NativeAOT) falls back to MinOpts (about 60 KB of IL, 2,000 basic blocks or 2,000 locals), maximum evaluation-stack depth, `localloc` inside loops. Add `--diagnose-limits` printing IL bytes, locals and block counts per function so SPEC gcc/perlbench/wrf hotspots become visible.
- [ ] **BE-26** (L, P1) Function splitting for over-limit methods: outline cold regions into helper methods sharing a frame struct so hot code stays optimized. Validate on 502.gcc_r, 500.perlbench_r and 521.wrf_r.
- [ ] **BE-27** (M, P1) Compile-time performance: measure link time and peak memory for ffmpeg (about 7,200 methods) and SPEC gcc, then parallelize per-function IL emission where `PersistedAssemblyBuilder` allows and cache the lowered bitcode by hash.

### Debugging and observability

- [ ] **BE-28** (L, P1) Debug information: consume `DISubprogram`/`DILocation` to emit sequence points and a portable PDB under `-g`, with local names where SSA allows, so .NET stack traces and debuggers show `file:line`; keep NativeAOT symbol maps. Done when a crashing C program prints the C source line in its stack trace.
- [ ] **BE-29** (M, P1) Fault mapping: `NullReferenceException`/`AccessViolationException` from bad C pointers become a "segmentation fault in <function>" report with the translated call stack; `LLVMNET_FAULT=abort|exception` selects behavior; memory-fault signal handlers are documented as unsupported.
- [ ] **BE-30** (M, P2) Optional instrumented mode (`--check-memory`) validating loads/stores against allocator metadata for debugging. Post-1.0 unless cheap.

### Vectors

- [ ] **BE-31** (S, P0) Scalarization remains the default. Fuzz the C fixtures with `-O3 -fvectorize -fslp-vectorize` and confirm every vector type LLVM produces either scalarizes or is rejected, never miscompiled.
- [ ] **BE-32** (L, P2) General fixed-vector lowering to `Vector128`/`Vector256` ([ADR-10](decisions.md#adr-10-simd-strategy)). Post-1.0.

## Exit criteria for M1

BE-04, BE-05, BE-07, BE-08, BE-11, BE-12, BE-15, BE-16, BE-18, BE-25 and BE-31 are done; the fuzzing job (FD-16) has run for two weeks without an open miscompile; the support matrix (BE-02) is published.
