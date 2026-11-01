# Troubleshooting

How to read what llvmnet says, and what to do about the common messages. [driver.md](driver.md#exit-status) lists the exit codes.

## Reading a diagnostic

Clang's and Flang's diagnostics pass through unchanged, colors and formats included, and the frontend's exit status becomes llvmnet's. llvmnet's own messages have the same form:

```
tests/c/computed-goto.c:2: error: Unsupported LLVM opcode 4
llvmnet: note: in function 'main'
llvmnet: note: while translating: indirectbr ptr %6, [label %8, label %7]
llvmnet: note: linked LLVM IR kept at computed-goto.dll.failed.bc
```

A compile error names the function and the LLVM instruction, with a `file:line:col` location when the input has debug information (`-g` or `-gline-tables-only`). The linked IR is kept next to the output as `<output>.failed.bc`, so the failure can be reproduced with `llvmnet <output>.failed.bc -o <output>` without the sources. An internal compiler error (exit status 3) adds a banner asking for a report with that file. `LLVMNET_TRACE=1` prints the .NET stack trace of any error.

## Common messages

| Message | Meaning and fix |
| --- | --- |
| `option '<option>' is not accepted: ...` | The driver rejects the option for the reason given; [driver.md](driver.md#not-accepted) lists them. In portable mode `-m<option>`, `--target=` and the layout options would change the fixed ABI. |
| `ABI mismatch: x.o was compiled for system-linux-x64-v1, but this link is dotnet64-v2` | Objects from both modes in one link; recompile everything with one `--runtime`. |
| `untagged LLVM input x.ll` | LLVM IR not produced by `llvmnet -c`; compile it with `llvmnet --runtime=<mode> -x ir -c x.ll -o x.o` so its ABI is recorded. |
| `Unresolved function: f` (portable) | Nothing defines `f`: a missing object or archive, or a service the sysroot does not have ([limitations.md](limitations.md)). |
| `Unresolved system ABI symbol f; add --system-library ...` (system) | No native library on the compiler host defines `f`; name the library with `--system-library`. |
| `duplicate definition of 'f' in b.o; it is already defined in a.o` | Two definitions in the link; archive members are shown as `lib.a(member.o)`. |
| `Cannot find LLVM bitcode archive libx.a in the -L directories` | `-lx` found no archive; add `-L<dir>`, or `-l:<file>` for an exact name. Native libraries need system mode and `--system-library`. |
| `The portable sysroot is missing from ...; build it with scripts/build-sysroot.sh` | In a checkout the sysroots are built by `scripts/ensure-built.sh`; a package has them under `sysroot/`. `LLVMNET_ROOT` names the toolchain root when the compiler is moved on its own. |
| `Unsupported LLVM opcode`, `Unsupported LLVM type`, `... is not supported` | A construct the backend rejects; [limitations.md](limitations.md#compiler) lists them with the diagnostic. |
| `Incompatible direct call signature` | A call whose argument types differ from the definition's in a way the CLR cannot call; [dev/calling-convention.md](../dev/calling-convention.md) states the accepted differences, and `--trap-missing-arguments` turns a void call with too few arguments into a trap instead. |
| `warning: emitting runtime trap: Invalid direct call through a prototype without parameters` | A call without arguments through a declaration such as `char f(void);` contradicts `f`'s definition. Configure probes do this on purpose, and the program links; it stops with `InvalidProgramException` only if the call runs. In a real program, include the header that declares `f` ([driver.md](driver.md#calls-that-contradict-their-definition)); `-w` silences the warning. |
| `NativeAOT prerequisite missing: ...` | The .NET 10 SDK, a C compiler, zlib, the aarch64 cross sysroot or a feed package is missing for a `--nativeaot` publish; the line names the fix ([deployment.md](deployment.md#nativeaot)). |
| `llvmnet: llvmnet programs need a 64-bit .NET runtime ...` (at run time) | The image was started on a host it cannot run on (32-bit, big-endian, Mono, .NET before 10); the line names the runtime to use. |
| `The framework 'Microsoft.NETCore.App', version '10.0.0' was not found` (at run time) | Only a later .NET major is installed; install the .NET 10 runtime, or set `DOTNET_ROLL_FORWARD=LatestMajor` to run untested on the newer one. |

## At run time

- **A segmentation fault** in C becomes a .NET `NullReferenceException` or `AccessViolationException`, reported by the CLR with the translated call stack; `abort` prints the C call stack. Generated code is not a sandbox: an invalid pointer can corrupt memory or end the process.
- **Descriptors left open** at exit are reported with `LLVMNET_CHECK_DESCRIPTORS=1`.
- **Indirect calls** through mismatched function-pointer types fail inside the CLR; `--check-indirect-calls` makes every indirect call check its target's signature and report the mismatch by name.
- **Large functions.** `--diagnose-limits` reports each function's IL size and locals and the ones the JIT compiles without optimization.
- **Threads and processes** in portable mode follow Linux semantics on every host, with the differences in [limitations.md](limitations.md#portable-mode): no `fork`, child processes through `posix_spawn`, `system` and `popen`, signals from inside the process delivered synchronously.
- **Time zones**: `TZ` and the host's zoneinfo files are used where they exist; elsewhere (Windows, a container without tzdata) the host layer generates zone data from .NET's `TimeZoneInfo` with numeric abbreviations. `LLVMNET_ZONEINFO=managed` forces the generated data.

## Keeping the intermediate files

- `--keep-ir <file>` copies the linked bitcode before lowering; `-emit-llvm -o x.bc` stops there; `-S -emit-llvm -c` writes a source's IR.
- `-###` prints the frontend commands with every option the driver added, so a compile can be repeated by hand.
- `-v` prints the commands as they run and a note for each option the driver accepted without effect.
