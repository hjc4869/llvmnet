# Native kernel packages

Codecs and math libraries keep their fastest code in assembly files (ffmpeg's and dav1d's nasm, OpenSSL's generated assembly, BLAS kernels), which llvmnet does not translate. A **native kernel package** ships that code beside a portable program: the project's build assembles its kernels natively for each runtime identifier it supports, and llvmnet calls them from the program's CIL, while the program keeps its C paths for every other host ([ADR-16](../plan/decisions.md#adr-16-simd-levels) Level 3). This is portable mode's; in system mode the program is native x86-64 code's peer and links such libraries with `--system-library`.

## A package

A package is a directory with `manifest.json` and the libraries it names:

```json
{
  "name": "dsp",
  "version": "1.0",
  "library": "dsp-kernels",
  "runtimes": {
    "linux-x64": { "shared": "runtimes/linux-x64/native/libdsp-kernels.so", "static": "runtimes/linux-x64/native/libdsp-kernels.a" },
    "linux-arm64": { "shared": "runtimes/linux-arm64/native/libdsp-kernels.so", "static": "runtimes/linux-arm64/native/libdsp-kernels.a" }
  },
  "symbols": { "dsp_add": {}, "dsp_wait": { "leaf": false } }
}
```

- `name` (lowercase letters, digits and dashes) is what `llvmnet_native_kernels` asks for; `library` is the libraries' name without prefix or extension (`libdsp-kernels.so`, `dsp-kernels.dll`, `libdsp-kernels.dylib`).
- `runtimes` lists the identifiers the package has, `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64` and the like, with a shared library for the JIT and a static one for NativeAOT, relative to the package's directory. Either may be missing: a package without a static library cannot be linked into a NativeAOT executable for that identifier, one without a shared library cannot run under the JIT there.
- `symbols` lists the functions the program calls in the package. A kernel is a leaf function by default: it calls nothing back in the program and returns soon, so a NativeAOT executable calls it without .NET's GC transition (`SuppressGCTransition`). `"leaf": false` keeps the transition, for a function that blocks or runs long. Under the JIT every kernel keeps the transition, because a call without it binds the library when the JIT compiles its caller, before the program could choose its C path.

The project's build makes the libraries with its native tools for each identifier, for example:

```sh
nasm -f elf64 kernel.asm -o kernel.o                      # or: clang --target=aarch64-linux-gnu -c kernel.S
ar rcs runtimes/linux-x64/native/libdsp-kernels.a kernel.o
cc -shared kernel.o -o runtimes/linux-x64/native/libdsp-kernels.so
```

## Building and running

```sh
llvmnet -O2 --native-kernels dsp/ program.c -o out/program.dll
dotnet out/program.dll
llvmnet -O2 --native-kernels dsp/ program.c --nativeaot -o program
```

Every function the program declares and a package lists is called in the package: directly, or through a function pointer, as DSP tables hold them. Under the JIT the image loads the library of the running host from `runtimes/<rid>/native/` beside it, where llvmnet copies every identifier's shared library; the library is loaded when a kernel is first called. A NativeAOT executable links the static library of its runtime identifier and binds the kernels directly, and `--aot-project` puts every identifier's static library into the project, linked when it is published for that identifier. The image lists its packages in its `llvmnet.kernels` metadata (`LlvmNet.Checks inspect`).

## Choosing the C path

On a host the package has no library for, the program must run its C paths, as the libraries do on a CPU without the instructions their assembly needs. Most libraries choose by detecting the CPU (CPUID in portable mode reports nothing on ARM64, so x86 kernels are never selected there; [x86-intrinsics.md](x86-intrinsics.md#cpu-detection)). A program can also ask whether the running host has the package's library:

```c
#include <llvmnet/cpu.h>
if (llvmnet_native_kernels("dsp")) dsp_table.add = dsp_add;   // 1 when this host has the library
```

A kernel called on a host without its library ends the process with exit status 127 and one line naming it:

```text
llvmnet: native kernel dsp_add of package dsp was called, but the package has no library for osx-arm64 (it has linux-x64, linux-arm64); the program's own CPU detection should have selected its C path here
```

## What crosses the boundary

Kernels take and return integers of up to 64 bits, pointers, `float` and `double`. Narrow integer arguments are extended as their `signext` or `zeroext` attribute says, which the native conventions expect, and narrow results are taken from their low bits. A kernel that is variadic or takes or returns a structure in memory (`byval`, `sret`) is a compile error naming it; a small structure that the System V convention passes in registers arrives as its integers, which only matches the native convention where the package's identifiers use System V too. The data a kernel reads is laid out by the portable ABI (`dotnet64-v2`), which agrees with every supported host for fixed-width types, pointers, `float` and `double`; declare kernels with `int64_t` rather than `long`, which is 32 bits on Windows. A kernel must not call back into the program.

A function-pointer call of a small kernel costs 2.7 ns as NativeAOT and 4.8 ns under the JIT, against 1.1 ns natively ([evidence](../evidence/2026-11-04-native-kernels.md)); kernels that do real work per call hide the difference.
