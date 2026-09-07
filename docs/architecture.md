# Architecture

The pipeline below is shared by three explicit runtime selections: legacy `managed-host`, native `system`, and versioned `portable`. [toolchain.md](toolchain.md) defines their ABI and deployment contracts; host-layout details below are not portable host-library interfaces.

## Pipeline

1. The driver invokes clang-22 for C/C++ or flang-22 for Fortran and emits LLVM bitcode. Frontend optimization defaults to O1 with automatic vectorization disabled; explicit frontend arguments can override defaults.
2. The small C++ bridge uses LLVM's parser, archive reader, linker, verifier, and exact numeric-constant APIs. It performs real lazy archive-member extraction. This bridge runs only inside the compiler process.
3. Executable links internalize definitions except the entry points and run global dead-code elimination. Library links preserve definitions. ABI tags reject incompatible object/archive members.
4. The C# backend reads LLVM through its C API. LLVM data layout determines sizes, alignments, and structure offsets.
5. `PersistedAssemblyBuilder` emits static CIL methods and a managed PE image. The .NET JIT executes those methods normally.
6. Optional NativeAOT consumes the emitted CIL executable directly. System-mode ABI bridges become static native inputs, permitting a single executable to be staged by ordinary build/benchmark tools.

## Values and Control Flow

LLVM SSA values map to typed CIL locals. Basic blocks use CIL labels. PHI copies are evaluated before any destination is updated, preserving parallel assignment on every incoming edge. Switch case values use the LLVM 22 switch API, not operand-number assumptions.

Integers through 32 bits use CIL `int32`, through 64 bits use `int64`, and through 128 bits use `UInt128` plus explicit signed/unsigned helpers. Narrow values are normalized to their LLVM bit width. Signed operations extend from that width before operating. Odd-width memory stores touch only the bytes occupied by the LLVM value.

LLVM `float` and `double` use their CIL equivalents. `x86_fp80` uses a 16-byte-layout software value with a 64-bit significand, full extended exponent range, and round-to-nearest-even arithmetic. Its constants are extracted bit-for-bit through LLVM. There is no silent long-double-to-double substitution. Other extended floating types remain unsupported.

`llvm.fmuladd` uses separately rounded multiplication and addition, which LLVM permits and which matches the generic native test baseline. `llvm.fma` remains fused. Saturation, overflow, ordered/unordered comparisons, signed zeros, and bit-width behavior have focused IR tests.

## Memory and Calls

Pointers are native integers into unmanaged memory or stack storage. Globals and constant aggregates are allocated before their initializers and relocations are installed. Function-pointer tables use `ldftn`; indirect calls use managed `calli`.

Arrays/structures are explicit-layout managed value types used as bit containers, not GC object graphs. Loads/stores use unaligned-safe CIL or exact-width helpers. Allocas use aligned stack allocation. `byval` parameters receive a private aligned copy; hidden `sret` pointers are preserved. Dynamic stack restoration and returns-twice calls are not yet implemented.

Variadic calls construct a software x86-64 SysV register-save and overflow area. Translated `va_start`, `va_copy`, and `va_arg` sequences and managed libc formatting functions consume the same layout. Variadic aggregate ABI classification is incomplete; scalar integer/pointer/float overflow-register cases are tested.

## Runtime

Managed exports are explicitly marked with `CExportAttribute` and checked against LLVM signatures. The runtime provides selected allocation, string, formatting/scanning, math, file, time, environment, pthread, atomic, signal, and filesystem interfaces. OS access goes through .NET APIs. P/Invoke is not used to execute native ffmpeg or substitute its algorithms.

Atomics deliberately use managed synchronization for correctness before performance. Pthreads map to managed threads, mutexes, condition waiters, once controls, and key destructors. This does not imply full pthread or POSIX support. Metadata such as `stat` and directory inode values is only partially modeled; ownership changes return an unsupported-operation error. Signal behavior cannot reproduce arbitrary native asynchronous stack interruption.

C++ `invoke` becomes a CIL try/catch region, landing pads select matching C++ exception types, and resume/rethrow propagates managed exceptions carrying the C++ object. Public single/multiple/virtual-base RTTI supports tested adjusted catches and dynamic casts. This is not a complete libstdc++ runtime or native C++ ABI.

Fortran uses Flang 22 CFI descriptors: a 24-byte header followed by rank-dependent 24-byte dimension records. Implemented intrinsic arrays preserve extents, lower bounds, and byte strides. Allocation, assignment, reductions, command arguments, and a subset of sequential formatted/list-directed I/O are managed runtime operations. Derived-type lifecycle, coarrays, complete I/O formats, and the full Flang runtime remain work in progress.

## Linking Boundaries

- LLVM bitcode linking is static and uses `-L` / `-l` or explicit object/archive paths.
- Managed linking uses `--cil-reference` and `CExport` methods.
- Native linking uses explicit `--native-import symbol=library!entrypoint` mappings and emits P/Invoke metadata.

These boundaries must not be conflated. In particular, a native function cannot consume a managed `ldftn` callback as an ordinary C callback. The explicit P/Invoke path rejects unsupported native aggregate signatures. System mode instead generates LLVM ABI bridge calls and unmanaged-callable callback methods. Portable mode uses only its own managed callback and header ABI and rejects native import options.