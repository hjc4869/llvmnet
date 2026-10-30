# C++ exception and RTTI bridge

The CLR, not libunwind, unwinds the stack of a translated program, so C++ exception handling cannot come entirely from libc++abi. This page is the contract between the backend, the managed bridge in `src/LlvmNet.Runtime/Cxx.cs`, and libc++/libc++abi in the portable sysroot. The decision is [ADR-02](../plan/decisions.md#adr-02-c-runtime-strategy).

## How a C++ exception travels

1. `throw` calls `__cxa_allocate_exception` (bridge) for the object and `__cxa_throw(object, type_info, destructor)` (bridge), which throws a managed `CxxException` holding the object pointer, its `type_info` and its destructor.
2. The backend lowers every `invoke` to a CIL `try` region. Its `catch (CxxException)` handler stores a GC handle to the exception as the landing pad's exception pointer and asks the bridge which clause matches: `Cxx.Selector` for catch clauses, `Cxx.Filter` for exception specifications. The selector value is the matched clause's type identifier, which `llvm.eh.typeid.for` returns for the same `type_info`; 1 for `catch (...)`, so type identifiers start at 2; 0 when only cleanups apply; and negative for a violated exception specification.
3. `__cxa_begin_catch` (bridge) returns the adjusted object pointer the matching clause selected and pushes the exception on the thread's catch stack. `__cxa_end_catch` pops it, and destroys and frees the object when the last catch ends without a rethrow.
4. Cleanups that do not catch end with `_Unwind_Resume` (bridge), which rethrows the same `CxxException` with its original stack trace.
5. `__cxa_rethrow` rethrows the exception on top of the catch stack. `std::terminate`, `__clang_call_terminate` and `__cxa_call_unexpected` reach the installed terminate handler, which must not return; so does `__cxa_pure_virtual` in system mode. An exception that no handler catches reaches it too, with the exception current: the program entry point and the portable thread start catch `CxxException` and call the terminate handler.

Landing pads catch only `CxxException`. Other managed exceptions (for example an `OutOfMemoryException` from the runtime, or the `NonLocalJumpException` of `longjmp`) pass through C++ frames without running their catch clauses, `catch (...)` or cleanups.

## Catch matching

In portable mode, libc++abi's `private_typeinfo.cpp` matches types. [ports/libcxxabi/llvmnet-catch.cpp](../../ports/libcxxabi/llvmnet-catch.cpp) exports `__llvmnet_cxx_can_catch(catch_type, thrown_type, &object)`, which asks libc++abi whether a clause accepts the thrown type and adjusts the object pointer to the base class or pointer value the handler receives. The image initializer registers it with the bridge. The same file defines `__gxx_personality_v0`, which every function with exception handling names, as a trap: the backend implements the personality semantics, so it is never called.

In system mode, catch matching and `__dynamic_cast` walk the Itanium `__class_type_info`, `__si_class_type_info` and `__vmi_class_type_info` layouts in C#, comparing `type_info` objects by address and then by name.

## Symbols the bridge owns

In portable mode the bridge provides exactly these symbols (`CilCompiler.MuslVisible` enforces the split); everything else in the C++ runtime comes from libc++ and libc++abi compiled to CIL:

| Area | Symbols |
| --- | --- |
| Throwing and catching | `__cxa_allocate_exception`, `__cxa_free_exception`, `__cxa_throw`, `__cxa_begin_catch`, `__cxa_end_catch`, `__cxa_rethrow`, `__cxa_get_exception_ptr`, `_Unwind_Resume` |
| `std::exception_ptr` (libc++abi ABI) | `__cxa_current_primary_exception`, `__cxa_increment_exception_refcount`, `__cxa_decrement_exception_refcount`, `__cxa_rethrow_primary_exception`, `__cxa_init_primary_exception` (`std::make_exception_ptr`) |
| Uncaught exceptions | `__cxa_uncaught_exceptions`, `__cxa_uncaught_exception`, `std::uncaught_exception`, `std::uncaught_exceptions` |
| Termination | `std::terminate`, `std::set_terminate`, `std::get_terminate`, `__cxa_call_unexpected`, `__clang_call_terminate` |
| Allocation failure | `std::set_new_handler`, `std::get_new_handler` (libc++abi's `operator new` calls them) |

libc++'s `std::exception_ptr`, `std::current_exception`, `std::rethrow_exception`, `std::nested_exception` and `std::uncaught_exceptions` are compiled unchanged and work through the entries above. libc++abi and musl provide guards (`__cxa_guard_*`), `__cxa_atexit`, `__cxa_finalize`, `__cxa_thread_atexit`, `operator new` and `delete`, `__dynamic_cast` and the `type_info` classes, `__cxa_bad_cast`, `__cxa_bad_typeid`, `__cxa_pure_virtual` and `__cxa_deleted_virtual` (which print a message and abort), `type_info` for the standard exception classes, and the demangler.

In system mode no C++ runtime is linked by default, so the bridge also provides `__dynamic_cast`, `__cxa_pure_virtual`, `operator new` and `delete`, guards, `__cxa_atexit`, `type_info` placeholders, the `std::__throw_*` helpers and the libstdc++ `exception_ptr` entry points that header-only libstdc++ code calls. The optional libstdc++ bitcode archive (`scripts/build-system-cxx.sh`) supplies the rest.

## Object layout and ownership

- The bridge allocates the thrown object without a `__cxa_exception` header in front of it. libc++abi's `cxa_exception.cpp` and `cxa_personality.cpp`, which read that header, are not part of the portable sysroot.
- Reference counting follows libc++abi: one reference for the in-flight exception and one per `exception_ptr`. The object's destructor runs and its memory is freed when the count reaches zero. A dependent rethrow (`std::rethrow_exception`) throws a new `CxxException` that shares the primary's object and count.

## Known limits

- System-mode catch matching handles public single, multiple and virtual inheritance, ambiguous bases, and pointer handlers by mangled name (the pointer value, added `const` or `volatile` at one level, `void *`, base-class pointees). Pointer-to-member catch clauses, multi-level qualification conversions and `dynamic_cast` through private bases are not handled.
- Type identifiers for `llvm.eh.typeid.for` live in a process-wide table (`Cxx.typeIds`).
- Cleanups are CIL `catch` blocks that rethrow, so the CLR finds a handler in every frame with destructors to run: when no C++ handler matches, destructors run before `std::terminate`. C++ leaves this implementation-defined ([except.terminate]); the Itanium runtime terminates without unwinding (libc++abi's `test_vector3`).
- libc++abi's exception globals (`__cxa_get_globals`, `__cxa_get_globals_fast`) and the unwinder interface (`_Unwind_Backtrace`, `_Unwind_ForcedUnwind`) are not provided: exceptions are managed objects and the CLR unwinds.
- A foreign (non-C++) managed exception is not a C++ exception: `std::current_exception` inside `catch (...)` does not capture it.

The behavior is covered by libc++abi's own tests (`tests/libcxx-conformance.sh libcxxabi`), `tests/cpp-filters.sh`, `tests/cpp-nothrow.sh`, `tests/cpp-terminate.sh`, `tests/exception-pointer.sh`, `tests/cpp.sh` (RTTI and catches) and the libc++ smoke program in `tests/libcxx.sh`.
