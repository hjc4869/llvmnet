# Architecture

llvmnet translates linked LLVM bitcode to CIL. The same pipeline serves both runtime modes, `portable` (the default) and `system`; [user/modes.md](../user/modes.md) defines their ABI and deployment contracts.

## Pipeline

1. The driver (`src/LlvmNet.Compiler/Driver.cs`) runs clang-22 for C and C++ or flang-22 for Fortran and emits LLVM bitcode. The default is `-O1 -fno-vectorize -fno-slp-vectorize`; explicit frontend options override it. Portable mode adds the sysroot's headers and a fixed target (`PortableAbi.cs`). Every object records its mode's ABI tag in the `llvmnet.abi` module flag.
2. A small C++ bridge to LLVM (`native/`, built as `libllvmnet-llvm.so`) links the bitcode with LLVM's linker, extracting archive members as a native linker does, and rejects inputs whose ABI tag differs from the link's. One `llvm::Linker` serves the whole link, because a linker indexes every struct type of its destination module when it is constructed (one per input made large links quadratic), and inputs are read lazily, so a function body is read only when its definition is linked. It also prepares modules for translation and builds the native call shims of system mode. It runs only inside the compiler.
3. Portable executables link musl's `crt1.o` and the sysroot archives (libomp, flang-rt, libc++, libc++abi, musl, compiler-rt builtins), extracting only the members the program uses; system executables link flang-rt from `sysroot/system-linux-x64-v1`.
4. `opt-22` runs the lowering passes. Executables internalize everything except the entry points and run global dead-code elimination; libraries keep their public definitions. Both expand vector reductions and masked memory intrinsics and run LLVM's scalarizer, which by default also scalarizes vector loads and stores; `--simd128` keeps those and the SIMD128 helper calls.
5. The C# backend reads the module through LLVM's C API. LLVM's data layout determines sizes, alignments and structure offsets.
6. `PersistedAssemblyBuilder` emits static CIL methods into a PE image, which the .NET JIT executes.
7. `--nativeaot` publishes the emitted assembly with NativeAOT. System-mode native bridges become static native inputs, so the result is one executable.

## Values and Control Flow

LLVM SSA values map to typed CIL locals and basic blocks to CIL labels. PHI copies are evaluated before any destination is updated, preserving parallel assignment on every incoming edge.

Integers up to 32 bits use CIL `int32`, up to 64 bits `int64`, and up to 128 bits `UInt128` with explicit signed and unsigned helpers; wider integers are rejected. Narrow values are normalized to their LLVM bit width, and signed operations sign-extend from that width first. Odd-width stores touch only the bytes the LLVM value occupies.

`float` and `double` use their CIL equivalents. `x86_fp80` uses `Float80`, a software value in 16-byte storage with a 64-bit significand, the full extended exponent range and round-to-nearest-even arithmetic; its constants are extracted bit for bit through LLVM. `half`, `bfloat` and `fp128` are rejected. `llvm.fmuladd` rounds the product and the sum separately, which LLVM permits; `llvm.fma` is fused.

Fixed-length vectors are scalarized by LLVM before translation. Vector values that remain at function boundaries use explicit-layout value storage with lane insertion and extraction, including bit-packed integer masks. Scalable vectors, target-specific intrinsics and some generic vector intrinsics are rejected; [user/support-matrix.md](../user/support-matrix.md) lists them.

LLVM 22's scalarizer can corrupt the terminator of an `invoke` that returns a vector. Before scalarization, llvmnet wraps such results in a one-field aggregate and extracts the vector in a new normal-edge block, preserving PHI predecessors and the unwind edge.

The opt-in [SIMD128 path](../user/simd128.md) maps exactly 128-bit vector storage to `Vector128<byte>` and keeps the SIMD128 helper calls and vector loads and stores. The helpers use typed `Vector128<T>` reinterpretations and can be inlined by the JIT and NativeAOT. The scalarizer still handles other vector arithmetic.

## Memory and Calls

Pointers are native integers into unmanaged memory. Globals and constant aggregates are allocated before their initializers and relocations are installed. Large initialized globals are embedded in data fields of at most 1 MiB and copied into one contiguous allocation before relocations are applied. Function pointers use `ldftn`, and calls to translated targets use `calli`.

Arrays and structures are explicit-layout value types used as bit containers. Loads and stores use unaligned-safe CIL or exact-width helpers. Allocas use aligned stack allocation; functions with stack save/restore or nonlocal-jump sites use scoped aligned allocations that are released on restore or unwind. `byval` parameters receive a private aligned copy, and hidden `sret` pointers are preserved.

Functions with more than 32,768 SSA values and no `setjmp` sites reuse primitive locals whose uses stay within one block, and keep values that cross blocks in typed per-call arrays, so they stay below the CIL local limit. Large aggregate-heavy or `setjmp` functions can still exceed CLR limits; `--diagnose-limits` reports the sizes.

Direct `setjmp` sites resume through a managed exception dispatcher in the owning activation, preserving live locals and restoring allocation checkpoints. `longjmp` cannot cross native frames. `sigsetjmp` saves the requested signal mask: the native one in system mode, the host layer's in portable mode.

Defined thread-local globals use lazy per-thread storage with initialization and destructor cleanup. External thread-local globals need a bitcode definition.

Variadic calls build a software x86-64 SysV register-save and overflow area, and translated `va_start`, `va_copy` and `va_arg` sequences, including musl's `printf` family, read the same layout. Aggregates passed to variadic functions follow the System V classification: one register slot per eightbyte of a small struct when they all fit, otherwise the overflow area, where `byval` arguments and `long double` also go.

C calls without a prototype may use a variadic LLVM signature for a fixed-arity definition. Direct calls then use the definition when the required argument types match, drop unused trailing arguments such as Fortran hidden character lengths, and discard an unused result. Direct calls also follow x86-64's register-slot rules: `i32` and `i64`, `ptr` and `i64`, and `i1` and `i8` are interchangeable in register positions. Other signature mismatches are compile errors. Missing required arguments are compile errors unless `--trap-missing-arguments` turns the call site into an exception; a call without arguments through a prototype without parameters, the shape of configure probes, always becomes one, with a warning. [dev/calling-convention.md](calling-convention.md) gives the full rules.

In system mode, translated callbacks passed to native code get unmanaged-callable entry points registered against their managed implementations, and indirect calls through those pointers resolve back to the managed method. Native variadic functions are called through LLVM-generated thunks with the call site's signature. Native calls into translated variadic or aggregate callbacks fail with a diagnostic.

## Runtime

`LlvmNet.Runtime` is a .NET assembly copied next to every output. Its public static methods marked with `CExportAttribute` resolve C symbols; the compiler checks them against the LLVM signatures. It contains:

- The host layer (`Host/`, portable mode): the Linux system calls musl issues, implemented over .NET for files, the file system, memory mappings, processes and signals, threads (musl's `clone` on managed threads, futexes) and time. [dev/host-abi.md](host-abi.md) is its contract.
- Compiler helpers: atomics, `Float80`, wide integers, math intrinsics, varargs, stack allocation, nonlocal jumps, thread-local storage, SIMD128 and `--check-indirect-calls`.
- The C++ exception bridge (`Cxx.cs`, [dev/cxx-eh-bridge.md](cxx-eh-bridge.md)).
- System-mode adapters: native callbacks, `pthread_create` and key destructors, and `atexit`/`exit` ordering.

Atomics on naturally aligned 8- to 64-bit locations use `Interlocked` and `Volatile`, so they are lock-free and interoperate with native atomics in system mode. Misaligned locations take a lock, and 128-bit atomics use a lock striped by address.

In portable mode, musl's pthreads run on managed threads, with the thread pointer and futexes provided by the host layer. In system mode, threads are native pthreads; an adapter wraps thread start and TLS destructors so that callbacks run inside .NET's thread lifetime. Neither mode reproduces native asynchronous signal delivery into arbitrary code; [dev/host-abi.md](host-abi.md) describes how portable mode delivers signals.

C++ `invoke` becomes a CIL try/catch region. Landing pads select matching C++ types and exception-specification filters, and resume or rethrow propagates a managed exception that carries the C++ object. In portable mode, catch matching, `type_info` and `dynamic_cast` come from libc++abi; in system mode the bridge implements them, and the optional libstdc++ archive (see [user/modes.md](../user/modes.md#optional-system-c-library)) supplies the library.

Fortran uses Flang 22 CFI descriptors and flang-rt in both modes: compiled against musl and libc++ in the portable sysroot, and against glibc in the system sysroot. System-mode OpenMP links LLVM's native `libomp`; outlined regions remain translated CIL and enter through native-callable thunks, and Flang's host `DO CONCURRENT` lowering uses the same path. Portable mode links LLVM's OpenMP runtime (libomp) from the sysroot, built on musl's pthreads with the port in `ports/openmp`; outlined regions are ordinary translated functions the runtime calls through its C invoker.

## Linking Boundaries

- LLVM bitcode linking is static and uses `-L`/`-l` or explicit object and archive paths.
- Managed linking uses `--cil-reference` and `CExport` methods.
- System mode resolves the remaining symbols in native libraries (the C library and `--system-library`) through generated LLVM bridges and unmanaged-callable callbacks.

Portable mode accepts only the first two: native libraries are rejected.
