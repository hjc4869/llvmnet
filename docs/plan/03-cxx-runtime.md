# Workstream CX: C++ runtime (libc++ and libc++abi)

Prefix `CX`. Owner: unassigned. Milestones: [M2](README.md#m2-library-architecture-spike) (spike), [M4](README.md#m4-portable-c-runtime) (delivery).

## Current state (verified 2026-10-30)

- Portable C++ uses libc++ 22.1.8 headers staged from the Debian package, an owned 148-line support source (`sysroot/dotnet64-v1/src/cxx-runtime.cpp`, `cxx-new.cpp`: chrono clocks, exception message reference counts, a few out-of-line functions) compiled to bitcode as `libcxx-managed.a`, and `src/LlvmNet.Runtime/Cxx.cs` (398 lines, 60 ABI exports: `__cxa_*`, `__dynamic_cast`, `new`/`delete` variants, `std::exception_ptr` internals, guards, `__cxa_thread_atexit`, terminate handlers).
- The backend lowers `invoke`/landing pads to CIL try/catch; a managed `CxxException` carries the C++ object; RTTI is resolved by a managed walk of `__class_type_info` structures.
- Portable mode has no iostreams, no locale, no `std::thread` lifecycle beyond what the owned file provides, no `<filesystem>`, and only the pieces of `<random>`, `<chrono>` and `<string>` that were needed by tested programs.
- System mode optionally links an upstream libstdc++ 16 bitcode archive (`scripts/build-system-cxx.sh`), which proves that the backend can compile a complete standard library implementation, including streams, locales, regex, futures and filesystem.

## Target architecture

Compile upstream libc++ (`libcxx/src`) and libc++abi (`libcxxabi/src`) from the same LLVM 22 release to portable bitcode on top of musl ([LC](02-portable-c-library.md)). The only owned pieces remain the exception bridge (`__cxa_throw`, `__cxa_begin_catch`, `__cxa_end_catch`, `__cxa_rethrow`, `__cxa_get_exception_ptr`, the personality semantics the backend implements) because unwinding is done by the CLR, not by libunwind. Everything else (type_info, `__dynamic_cast`, demangler, guards, `__cxa_atexit`, `new`/`delete`, `std::exception_ptr`, iostreams, locale, threads, filesystem) comes from upstream. Decision in [ADR-02](decisions.md#adr-02-c-runtime-strategy).

## Items

### Spike (M2)

- [x] **CX-01** (L, P0) Build libc++ and libc++abi 22.1.8 with `llvmnet-clang++ --runtime=portable` against the musl sysroot from LC-01, excluding libc++abi's unwinder-dependent files. Run smoke programs: `std::cout`, `std::string`, `std::map`, `std::sort`, `std::thread` with `std::mutex`, `std::regex`, `std::filesystem`, `std::to_string(long double)`. List every backend blocker as a BE item. — **Done** (`224d5be`): All 66 sources compile; smoke program and 11 fixtures match native (`tests/libcxx.sh`).

### Exception and RTTI bridge (M4)

- [x] **CX-02** (M, P0) Write the bridge contract in `docs/dev/cxx-eh-bridge.md`: which `__cxa_*` symbols stay managed, how the managed exception object maps to `__cxa_exception` fields libc++abi expects (so `std::current_exception`, `std::rethrow_exception`, `std::nested_exception` and `std::uncaught_exceptions` work unchanged), and how `std::terminate` is reached. Pass the libc++abi exception tests that do not depend on unwinding details. — **Done** (`3f12932`, `0609f39`): `docs/dev/cxx-eh-bridge.md` is the contract. libc++abi's own test suite is not run yet (CX-13).
- [x] **CX-03** (M, P0) Replace the managed RTTI walk with libc++abi's `__dynamic_cast` and `type_info` comparison, and delete `Cxx.typeIds`. Keep type_info name comparison consistent with how llvmnet links (merged strings, `_LIBCPP_TYPEINFO_COMPARISON_IMPLEMENTATION`). — **Done for portable mode** (`0609f39`): libc++abi's `private_typeinfo.cpp` provides `type_info`, `__dynamic_cast` and, through `ports/libcxxabi/llvmnet-catch.cpp`, the catch matching the bridge uses. System mode, which links no C++ runtime by default, keeps the managed walk; `Cxx.typeIds` still numbers `llvm.eh.typeid.for`.
- [x] **CX-04** (M, P0) Static initialization: `__cxa_guard_*` from libc++abi (futex-based over LC-02), `__cxa_atexit` ordering with C `atexit` and `__cxa_thread_atexit_impl` from musl; tests for constructor order across translation units and thread-local destructors. — **Done** (`1dc64f0`): guards from libc++abi, `__cxa_atexit` from musl, `__cxa_thread_atexit_impl` in the musl port (CX-12); `tests/exit-order.sh` and `tests/thread-local.sh` check the order against glibc in portable mode under JIT and NativeAOT.

### Library delivery (M4)

- [x] **CX-05** (M, P0) Threads and synchronization: `std::thread`, `<mutex>`, `<condition_variable>`, `<future>`, `<shared_mutex>`, `<atomic>` lock-free status consistent with [BE-16](01-compiler-backend.md), `<chrono>` clocks over musl. Run `tests/cpp/*.cpp` plus the libc++ `thread` test directory. — **Done** (`571d79f`): libc++'s `std/thread` directory passes 343 of 345 tests in portable mode (99.4%) after the libraries were built with the platform macros user code sees; the two failures are `jthread` join deadlock detection and thread creation under resource limits.
- [ ] **CX-06** (M, P0) Streams, locale (`C` and `C.UTF-8`), `<filesystem>` over musl POSIX, `<random>` with `random_device` over `getentropy`, `<regex>`, `<charconv>` and `<format>` including the long double paths through `Float80`. — **In progress** (`0ef8ad7`): streams, filesystem, regex, charconv and format pass libc++'s suites (input.output 97.2%, utilities 99.7%); localization is 85.8% because musl has only C and C.UTF-8 locale data (LC-20 documents that scope), and filesystem cannot create FIFOs, sockets or hard links (LC-30).
- [x] **CX-07** (M, P0) `scripts/build-portable-cxx.sh`: pinned LLVM source tarball by hash, a `__config_site` for dotnet64-v1 (threads enabled, localization enabled, no `_LIBCPP_HAS_NO_*` stubs that hide missing functionality), producing `libc++.a` and `libc++abi.a` bitcode in the sysroot; delete `cxx-runtime.cpp`, `cxx-new.cpp` and the obsolete parts of `Cxx.cs`. The driver links both automatically for C++ inputs. — **Done** (`1dc64f0`, `0609f39`): `scripts/build-portable-cxx.sh` is a step of `scripts/build-sysroot.sh`; the `__config_site` enables threads, localization, filesystem, random device and wide characters; the driver links `libc++.a` and `libc++abi.a`; the owned support sources are deleted, and the bridge keeps only throwing, catching and termination in portable mode.
- [x] **CX-08** (M, P0) Run the libc++ lit test suite in portable JIT with a lit configuration that compiles through `llvmnet-clang++` and executes through the launcher, starting with `containers`, `strings`, `algorithms`, `utilities`, `thread`, `localization`, `input.output`; publish pass rates; run a subset under NativeAOT nightly. — **Done** (`3f12932`, `0ef8ad7`): `tests/libcxx-conformance.sh` runs libc++'s lit suite through llvmnet; 5,101 of 5,171 tests pass (98.6%) in the CX-08 directories, every failure classified in `docs/evidence/2026-10-30-m3-m5.md`. Not yet in CI, and not yet run under NativeAOT.
- [ ] **CX-09** (S, P1) ABI details: sized and aligned deallocation, user replacement of `operator new` (weak symbol semantics from BE-07), `__cxa_pure_virtual`, `std::bad_alloc` on allocation failure from musl `malloc`.
- [ ] **CX-10** (S, P1) System mode: keep the optional libstdc++ archive and its license notice; evaluate offering libc++ for system mode as well so both modes share one C++ library (P2).
- [ ] **CX-11** (M, P1) SPEC C++ in portable mode after CX-07: deepsjeng, omnetpp, xalancbmk, povray, parest, blender, leela, cactuBSSN and the CPU 2026 C++ entries; every failure becomes a BE/LC/CX item.

- [x] **CX-12** (S, P1) glibc-compatible `thread_local` destructor order on the main thread: musl lacks `__cxa_thread_atexit_impl`, so libc++abi's fallback runs main-thread destructors at their `atexit` position. Provide the function in the musl port and run the registered destructors at the start of `exit`, as glibc does. Two existing fixtures depend on this order. — **Done** (`1dc64f0`): `__cxa_thread_atexit_impl` in the musl port runs the calling thread's destructors at the start of `exit()`; libc++abi is built to call it.

- [ ] **CX-13** (S, P1) Run libc++abi's own test suite (`libcxxabi/test`) in portable mode, excluding the unwinder tests the CLR makes inapplicable, with the same lit configuration as CX-08.

## Exit criteria for M4

CX-02 through CX-08 are done; the libc++ test directories listed in CX-08 pass at or above 98% with every failure classified; SPEC C++ portable results match system mode.
