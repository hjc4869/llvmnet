# Workstream FT: Fortran runtime (flang-rt)

Prefix `FT`. Owner: unassigned. Milestones: [M2](README.md#m2-library-architecture-spike) (spike), [M5](README.md#m5-fortran-runtime) (delivery).

## Current state (verified 2026-10-30)

- 126 hand-written `_FortranA*` entry points across fourteen files in `src/LlvmNet.Runtime` (about 2,600 lines): formatted, list-directed, namelist, unformatted and direct I/O; descriptors; allocation; intrinsic assignment; a subset of reductions (`SUM` for four kinds only); `PACK`/`RESHAPE`/`TRANSFER`/`SPREAD`; random numbers; inquiries; date/time; array constructors; derived-type lifetime.
- Explicitly unsupported today: final procedures, defined I/O and defined assignment, general owned-component assignment, parameterized derived types, formatted direct access, segmented records, value stacks, `REAL(16)`, many editing descriptors, `MATMUL`/`MAXVAL`/`MINLOC`/`PRODUCT` and the other reductions not yet needed by a workload.
- Every installed SPEC Fortran entry passed in system mode with this runtime; portable failures are dominated by C/C++ library gaps rather than Fortran ones. Flang's upstream runtime was never compiled through llvmnet.

## Target architecture

Compile `flang-rt` (the LLVM 22 Fortran runtime, C++17) with llvmnet on top of musl and libc++ ([LC](02-portable-c-library.md), [CX](03-cxx-runtime.md)) and link it as `libflang_rt.runtime.a` bitcode. The hand-written runtime is deleted. `REAL(16)` stays rejected until [BE-14](01-compiler-backend.md) exists, so flang-rt is built without quad-precision support. Decision in [ADR-03](decisions.md#adr-03-fortran-runtime-strategy).

## Items

### Spike (M2)

- [x] **FT-01** (L, P0) Build `flang-rt/lib/runtime` from the LLVM 22 source tarball with `llvmnet-clang++ --runtime=portable` against the musl + libc++ sysroot, excluding quadmath. Run `print *, "hello"`, formatted real output, an `ALLOCATE`/`DEALLOCATE` loop, a namelist read, and `exchange2`-style array code. Record blockers (long double formatting, `__float128` references, large static tables, `std::` features) as BE/LC/CX items. — **Done** (`a71ed1c`): All 67 sources compile; 34 fixtures match native Flang (`tests/flang-rt.sh`).

### Delivery (M5)

- [ ] **FT-02** (M, P0) Program glue: `_QQmain` entry, `_FortranAProgramStart`/`EndStatement`, command-line arguments, `STOP`/`ERROR STOP` exit codes, and environment handling (`FORT_CONVERT`, `FORT_FMT_RECL`, `NO_STOP_MESSAGE`) as flang-rt implements them.
- [ ] **FT-03** (M, P0) I/O units over musl POSIX (`open`/`read`/`write`/`lseek`): console units 5/6/0, scratch files, `INQUIRE`, direct/stream access, asynchronous I/O reported as unsupported by flang-rt itself.
- [ ] **FT-04** (M, P0) Numeric kinds: `REAL(10)` through flang-rt's long double paths exercises `Float80` heavily; `REAL(16)` produces a Flang-level diagnostic; integer kinds 1/2/4/8/16; `COMPLEX` of each supported kind; IEEE intrinsic modules.
- [ ] **FT-05** (M, P0) `scripts/build-portable-flang-rt.sh`: pinned source by hash, produce the runtime archive and install intrinsic `.mod` files (`iso_c_binding`, `iso_fortran_env`, `ieee_*`, later `omp_lib`) into the sysroot; the driver links the archive for Fortran inputs; delete `Fortran*.cs`.
- [ ] **FT-06** (M, P0) Conformance: flang-rt's gtest unit tests compiled through llvmnet where feasible, `llvm-test-suite/Fortran` (including the gfortran test port), and the NIST FCVS F77 suite; publish pass rates. Expected to clear final procedures, defined I/O, PDTs and namelist completeness without llvmnet-specific code.
- [ ] **FT-07** (M, P0) SPEC Fortran portable parity: exchange2, roms, wrf, cam4, pop2, bwaves, fotonik3d, cactuBSSN and the CPU 2026 Fortran entries; each failure becomes an item.
- [ ] **FT-08** (M, P1) Portable OpenMP: build LLVM's `openmp` runtime (`libomp`) over musl pthreads, provide `omp_lib` modules, validate `tests/c/openmp.c` and Fortran `DO CONCURRENT` host lowering, and define thread-count semantics on .NET. Extends [ADR-07](decisions.md#adr-07-portable-threading-model).
- [ ] **FT-09** (S, P1) System mode: build the same flang-rt bitcode against host headers so both modes share one Fortran runtime, or document why the system mode keeps a separate build.

## Exit criteria for M5

FT-02 through FT-07 are done; FCVS and the `llvm-test-suite/Fortran` subset pass at or above 98% with failures classified; SPEC Fortran portable results match system mode.
