# Calls and signature compatibility

This page states which caller/callee signature differences llvmnet accepts (plan item BE-18). It follows from how LLVM IR is lowered: every LLVM function becomes a static CIL method, direct calls become `call`, and calls through pointers become `calli` with the call site's signature.

## LLVM to CIL types

| LLVM type | CIL type |
| --- | --- |
| `void` | `void` |
| `i1` to `i32` | `int32` (values normalized to their bit width) |
| `i33` to `i64` | `int64` |
| `i65` to `i128` | `System.UInt128` |
| `ptr` (any address space) | native `int` |
| `float`, `double` | `float32`, `float64` |
| `x86_fp80` | `LlvmNet.Runtime.Float80` |
| structs, arrays, vectors | a generated explicit-layout value type per LLVM type (`Vector128<byte>` for 128-bit vectors under `--simd128`) |
| variadic function | the fixed parameters plus one trailing native `int` pointing at the software `va_list` area |

## Direct calls

The callee's definition is known, so llvmnet checks the call site against it at compile time.

- An identical LLVM function type is always accepted.
- A call site whose LLVM type is variadic, as produced by a K&R or no-prototype call, may call a fixed-arity definition when the leading argument types match. Unused trailing arguments, including Flang's hidden character lengths, are dropped.
- A `void` call site may call a definition that returns a value; the value is discarded. A non-`void` call site may call a `void` definition only when the result is unused.
- In both modes, these x86-64 SysV register-slot equivalences are accepted, because native C and Fortran code relies on them (BE-39 extended them to portable mode, whose `dotnet64-v2` ABI follows x86-64's): `i32` and `i64` integer arguments in register positions, `i1` and `i8`, and `ptr` and `i64`. Flang passes hidden character lengths as `i64` to C functions declared with `int`, for example. Aggregates, `byval`, `sret`, `inreg` and `signext` parameters, and non-C calling conventions are never adjusted.
- Calling with fewer arguments than the definition requires is a compile error. `--trap-missing-arguments` instead emits a trap at such a `void` call so a program whose invalid path is never executed can still be built.
- Every other difference is a compile error ("Incompatible direct call signature").

## Indirect calls

A function pointer is a raw method address, and `calli` uses the call site's CIL signature with no runtime check. The CLR therefore behaves correctly exactly when the call site's CIL signature, from the table above, equals the target method's CIL signature.

- Casts between pointer types are always safe: `int (*)(char *)` and `int (*)(void *)` are both `int32(native int)`.
- Integer types of the same CIL class are safe: `i8`, `i16` and `i32` are all `int32`.
- Anything else is undefined behavior in the CLR, as it is in C: `int` versus `long`, `float` versus `double`, a different parameter count, aggregate versus scalar, or variadic versus fixed. The JIT does not detect these, so results can be wrong values, corrupted registers or crashes.
- In system mode, calls through pointers to native functions go through generated native bridges; native code calling translated callbacks is described in [toolchain.md](../toolchain.md#system-abi).

### Finding incompatible indirect calls

`--check-indirect-calls` records each address-taken translated function's CIL signature when its pointer is created and checks it at every indirect call:

```text
System.InvalidProgramException: Indirect call with CIL signature Int32(Int32) reached widen,
whose signature is Int64(Int64); ...
```

The check costs a dictionary lookup per indirect call and per function-pointer creation, so it is a debugging option, not a default. Pointers that were never registered, such as native functions in system mode, are not checked. `tests/indirect-calls.sh` exercises it.
