# Architecture

llvmnet translates linked LLVM bitcode to CIL. The same pipeline serves both runtime modes, `portable` (the default) and `system`; [user/modes.md](../user/modes.md) defines their ABI and deployment contracts.

## Pipeline

1. The driver (`src/LlvmNet.Compiler/Driver.cs`) runs clang-22 for C and C++ or flang-22 for Fortran and emits LLVM bitcode. The default is `-O1 -fno-vectorize -fno-slp-vectorize`; explicit frontend options override it. Portable mode adds the sysroot's headers and a fixed target (`PortableAbi.cs`). Every object records its mode's ABI tag in the `llvmnet.abi` module flag.
2. A small C++ bridge to LLVM (`native/`, built as `libllvmnet-llvm.so`) links the bitcode with LLVM's linker, extracting archive members as a native linker does, and rejects inputs whose ABI tag differs from the link's. One `llvm::Linker` serves the whole link, because a linker indexes every struct type of its destination module when it is constructed (one per input made large links quadratic), and inputs are read lazily, so a function body is read only when its definition is linked. It also prepares modules for translation and builds the native call shims of system mode. It runs only inside the compiler.
3. Portable executables link musl's `crt1.o` and the sysroot archives (libomp, flang-rt, libc++, libc++abi, musl, compiler-rt builtins), extracting only the members the program uses; system executables link flang-rt from `sysroot/system-linux-x64-v1`.
4. `opt-22` runs the lowering passes. Executables internalize everything except the entry points and run global dead-code elimination; libraries keep their public definitions. Both expand vector reductions and masked memory intrinsics. Vector code stays whole for the typed vector lowering, which scalarizes the functions it does not translate in-process through LLVM's scalarizer (`native/vectors.cpp`); `--simd128` and the hidden `--vector-lowering=scalar` run the scalarizer on everything instead, as before the lowering, and `--vector-lowering=typed` scalarizes only what the lowering does not translate.
5. The C# backend reads the module through LLVM's C API. LLVM's data layout determines sizes, alignments and structure offsets.
6. `PersistedAssemblyBuilder` emits static CIL methods into a PE image, which the .NET JIT executes. Functions are methods of a class `Program`; because the CLR allows at most 65,535 methods per type, internal methods beyond 32,768 go to further internal classes named `Program` in namespaces `LlvmNet.Methods<n>` (exports and the entry methods stay on `Program`), as static fields beyond 32,768 go to `__Statics<n>` classes.
7. `--nativeaot` publishes the emitted assembly with NativeAOT. System-mode native bridges become static native inputs, so the result is one executable.

## Values and Control Flow

LLVM SSA values map to typed CIL locals and basic blocks to CIL labels. PHI copies are evaluated before any destination is updated, preserving parallel assignment on every incoming edge.

Integers up to 32 bits use CIL `int32`, up to 64 bits `int64`, and up to 128 bits `UInt128` with explicit signed and unsigned helpers; wider integers are rejected. Narrow values are normalized to their LLVM bit width, and signed operations sign-extend from that width first. Odd-width stores touch only the bytes the LLVM value occupies.

Integer and pointer `select`, `llvm.abs` and the integer min/max intrinsics are computed with masks instead of branches (`false ^ ((true ^ false) & mask)`, `(x ^ s) - s`), because RyuJIT turns a branch into a conditional move only outside loops; a select whose condition orders two integers of at most 32 bits takes its mask from the sign of their 64-bit difference, and so do min/max of such integers. Min/max against a constant bound keep a branch: such clamps almost always go the same way, and a predicted branch costs less than a mask. Floating-point, 128-bit and aggregate selects keep a branch, which is exact for every value.

`float` and `double` use their CIL equivalents. `x86_fp80` uses `Float80`, a software value in 16-byte storage with a 64-bit significand, the full extended exponent range and round-to-nearest-even arithmetic; its constants are extracted bit for bit through LLVM. `half`, `bfloat` and `fp128` are rejected. `llvm.fmuladd` rounds the product and the sum separately, which LLVM permits; `llvm.fma` is fused.

Fixed-length vectors of 8- to 64-bit integers, pointers, `float`, `double` and `i1` are lowered to `Vector128<T>` operations (ADR-16 Level 1, `VectorEmitter.cs`), which RyuJIT and ILC compile to the host's SIMD instructions. A vector of at most 128 bits is one `Vector128<T>`, its unused upper lanes undefined (loads and stores touch exactly its store size: 8 bytes for `<2 x float>`, 12 for `<3 x i32>`); a wider one is a struct of 128-bit fragments, which RyuJIT keeps in separate registers. Integer lanes are signed and reinterpreted (`As`, free) where an operation needs another view; an `i1` vector is a mask of all-ones or zero lanes whose width follows from its lane count (16 lanes of 8 bits, 8 of 16, 4 of 32, 2 of 64), and a comparison whose own lanes have another width is computed again at its use instead of converted. Lane-width changes (`sext`, `trunc`, `fpext`, the conversions, masks) go through `Widen` and `Narrow`, `shufflevector` through constant `Shuffle`, half `Create` or `ConditionalSelect` per result fragment, and what no vector instruction does (integer division, variable shifts, partial memory accesses, mask bits) through `LlvmNet.Runtime.Vectors`. Reductions in optnone functions, which `expand-reductions` skips, are lowered directly, so `-O0` vector code runs. Parameters are typed vectors (x64 passes them on the stack, ARM64 in vector registers); a vector result of at most 16 bytes returns as its explicit-layout bytes, because .NET returns a `Vector128` through a hidden buffer, and NativeAOT does not make a `musttail` call through a function pointer with such a result a tail call.

Before translation, LLVM's scalarizer turns into scalar code every function whose vector code the lowering does not translate (lanes of no CIL vector type, such as `i24` or `i128`, vector `getelementptr`, vector intrinsics without a lowering) and every function whose vector code moves more lanes than it computes on: each lane inserted or extracted costs an instruction as a `Vector128` and none as scalars, and each operation on N lanes saves N - 1 scalar ones. That keeps vectorized loops as vector code and the x86-64 ABI's two-float `<2 x float>` and SROA's small arrays as scalar code. Vector values that remain in scalarized functions with lanes of no CIL vector type use explicit-layout value storage with lane insertion and extraction, including bit-packed integer masks. `llvmnet -v` names each scalarized function and why. Scalable vectors, target-specific intrinsics and some generic vector intrinsics are rejected; [user/support-matrix.md](../user/support-matrix.md) lists them.

LLVM 22's scalarizer can corrupt the terminator of an `invoke` that returns a vector. Before scalarization, llvmnet wraps such results in a one-field aggregate and extracts the vector in a new normal-edge block, preserving PHI predecessors and the unwind edge.

The opt-in [SIMD128 path](../user/simd128.md) maps exactly 128-bit vector storage to `Vector128<byte>` and keeps the SIMD128 helper calls and vector loads and stores. The helpers use typed `Vector128<T>` reinterpretations and can be inlined by the JIT and NativeAOT. The scalarizer handles other vector arithmetic there, until SV-07 moves the path onto the typed lowering.

## Memory and Calls

Pointers are native integers into unmanaged memory. Globals and constant aggregates are allocated before their initializers and relocations are installed. Large initialized globals are embedded in data fields of at most 1 MiB and copied into one contiguous allocation before relocations are applied. Function pointers use `ldftn`, and calls to translated targets use `calli`.

Arrays and structures are explicit-layout value types used as bit containers. Loads and stores use unaligned-safe CIL or exact-width helpers. Allocas use aligned stack allocation; functions with stack save/restore or nonlocal-jump sites use scoped aligned allocations that are released on restore or unwind. `byval` parameters receive a private aligned copy, and hidden `sret` pointers are preserved.

Functions with more than 32,768 SSA values and no `setjmp` sites reuse primitive locals whose uses stay within one block, and keep values that cross blocks in typed per-call arrays, so they stay below the CIL local limit. Large aggregate-heavy or `setjmp` functions can still exceed CLR limits; `--diagnose-limits` reports the sizes.

Direct `setjmp` sites resume through a managed exception dispatcher in the owning activation, preserving live locals and restoring allocation checkpoints. `longjmp` cannot cross native frames. `sigsetjmp` saves the requested signal mask: the native one in system mode, the host layer's in portable mode.

A `musttail` call becomes a CIL `tail.` call followed by `ret`. The JIT honors it as a fast tail call that reuses the frame or, when it cannot, through the runtime's tail-call helpers; NativeAOT has no helpers, and on x64 refuses a fast tail call from a method that uses `localloc`. A function with a `musttail` call therefore takes its allocas and variadic call area from the scoped allocation arena, and a tail call inside the arena's (or a `setjmp` dispatcher's) protected region first leaves it, so the `finally` releases the arena before the call. Calls a fast tail call cannot carry are compile errors: `byval` arguments, arguments or results of more than 16 bytes, and forwarded variadic arguments. Windows x64 passes 16-byte values by reference too, so there NativeAOT makes a `musttail` call with a vector, `long double` or 128-bit argument an ordinary call. Ordinary `tail` calls are plain calls: an explicit tail call is never inlined.

Defined thread-local globals use lazy per-thread storage with initialization and destructor cleanup. External thread-local globals need a bitcode definition.

Variadic calls build a software x86-64 SysV register-save and overflow area, and translated `va_start`, `va_copy` and `va_arg` sequences, including musl's `printf` family, read the same layout. Aggregates passed to variadic functions follow the System V classification: one register slot per eightbyte of a small struct when they all fit, otherwise the overflow area, where `byval` arguments and `long double` also go.

C calls without a prototype may use a variadic LLVM signature for a fixed-arity definition. Direct calls then use the definition when the required argument types match, drop unused trailing arguments such as Fortran hidden character lengths, and discard an unused result. Direct calls also follow x86-64's register-slot rules: `i32` and `i64`, `ptr` and `i64`, and `i1` and `i8` are interchangeable in register positions. Other signature mismatches are compile errors. Missing required arguments are compile errors unless `--trap-missing-arguments` turns the call site into an exception; a call without arguments through a prototype without parameters, the shape of configure probes, always becomes one, with a warning. [dev/calling-convention.md](calling-convention.md) gives the full rules.

In system mode, translated callbacks passed to native code get unmanaged-callable entry points registered against their managed implementations, and indirect calls through those pointers resolve back to the managed method. Native variadic functions are called through LLVM-generated thunks with the call site's signature. Native calls into translated variadic or aggregate callbacks fail with a diagnostic.

## Runtime

`LlvmNet.Runtime` is a .NET assembly copied next to every output. Its public static methods marked with `CExportAttribute` resolve C symbols; the compiler checks them against the LLVM signatures. It contains:

- The host layer (`Host/`, portable mode): the Linux system calls musl issues, implemented over .NET for files, the file system, memory mappings, processes and signals, threads (musl's `clone` on managed threads, futexes) and time. [dev/host-abi.md](host-abi.md) is its contract.
- Compiler helpers: atomics, `Float80`, wide integers, math intrinsics, varargs, stack allocation, nonlocal jumps, thread-local storage, vector lanes (`Vectors`), SIMD128 and `--check-indirect-calls`.
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

Portable mode accepts only the first two: native libraries are rejected. A native extension ([user/extensions.md](../user/extensions.md)) is the second kind: a generated managed reference whose stubs call a host library they load at run time, registered with the runtime's `dlopen` at start.
