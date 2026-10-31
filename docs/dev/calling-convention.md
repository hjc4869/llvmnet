# Calls and signature compatibility

This page states which caller/callee signature differences llvmnet accepts. It follows from how LLVM IR is lowered: every LLVM function becomes a static CIL method, direct calls become `call`, and calls through pointers become `calli` with the call site's signature. The rules are implemented in `src/LlvmNet.Compiler/FunctionEmitter.cs`.

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

The `va_list` area mirrors x86-64's: a register save area of six 8-byte general slots and eight 16-byte floating-point slots, then an overflow area, with the `__va_list_tag` the callee's `va_start` copies in front. Clang lowers `va_arg` to the x86-64 rules, so the caller (`src/LlvmNet.Compiler/VarArgEmitter.cs`) places each variadic argument as the System V ABI classifies it: integers and pointers in general slots, `float` and `double` in floating-point slots, `long double` and aggregates passed `byval` in the overflow area, and a small aggregate, which Clang passes as its eightbytes (`{ i64, double }`), in one slot per eightbyte when they all fit, otherwise in the overflow area. `tests/varargs-aggregates.sh` compares the result with native.

## Direct calls

The callee's definition is known, so llvmnet checks the call site against it at compile time. The same rules apply in both runtime modes; `dotnet64-v2` follows x86-64's calling convention.

- An identical LLVM function type is always accepted.
- A call that passes more arguments than a fixed-arity definition takes, as K&R and unprototyped calls do, is accepted when the leading argument types match. The extra arguments, such as Flang's hidden character lengths, are dropped.
- A `void` call site may call a definition that returns a value; the value is discarded. A non-`void` call site may call a `void` definition only when the result is unused.
- Native C and Fortran code relies on x86-64 register slots for some mismatches, so these are accepted for C calling-convention calls: an `i64` argument for an `i32` parameter (the low 32 bits are passed), an `i32` argument for an `i64` parameter in the first six positions, `i1` and `i8`, `ptr` and `i64`, and `i8` and `[1 x i8]` for arguments and returns. Flang passes hidden character lengths as `i64` to C functions declared with `int`, and `character(1)` values as `[1 x i8]` where C takes `char`, for example. An argument is never adjusted when the callee returns an aggregate other than a byte array, or when that parameter or an earlier one is not an integer of up to 64 bits, `float`, `double`, pointer or byte array, or has a `byval`, `sret`, `inreg` or `signext` attribute (`signext` is allowed on a one-byte conversion).
- Calling with fewer arguments than the definition requires is a compile error. With `--trap-missing-arguments`, such a call to a `void` function compiles to a run-time trap instead, so a program whose invalid path never runs can still be built.
- Every other difference is a compile error ("Incompatible direct call signature").

## Indirect calls

A function pointer is a raw method address, and `calli` uses the call site's CIL signature with no runtime check. The CLR therefore behaves correctly exactly when the call site's CIL signature, from the table above, equals the target method's CIL signature.

- Casts between pointer types are always safe: `int (*)(char *)` and `int (*)(void *)` are both `int32(native int)`.
- Integer types of the same CIL class are safe: `i8`, `i16` and `i32` are all `int32`.
- Anything else is undefined behavior in the CLR, as it is in C: `int` versus `long`, `float` versus `double`, a different parameter count, aggregate versus scalar, or variadic versus fixed. The JIT does not detect these, so results can be wrong values, corrupted registers or crashes.
- In system mode, calls through pointers to native functions go through generated native bridges; native code calling translated callbacks is described in [user/modes.md](../user/modes.md#system-abi).

### Finding incompatible indirect calls

`--check-indirect-calls` records each address-taken translated function's CIL signature when its pointer is created and checks it at every indirect call:

```text
System.InvalidProgramException: Indirect call with CIL signature Int32(Int32) reached widen,
whose signature is Int64(Int64); ...
```

The check costs a dictionary lookup per indirect call and per function-pointer creation, so it is a debugging option, not a default. Pointers that were never registered, such as native functions in system mode, are not checked. `tests/indirect-calls.sh` exercises it.
