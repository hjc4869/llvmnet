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
  "symbols": { "dsp_add": {}, "dsp_wait": { "leaf": false }, "dsp_reset": { "optional": true } },
  "data": ["dsp_weights", "dsp_counters"]
}
```

- `name` (lowercase letters, digits and dashes) is what `llvmnet_native_kernels` asks for; `library` is the libraries' name without prefix or extension (`libdsp-kernels.so`, `dsp-kernels.dll`, `libdsp-kernels.dylib`).
- `runtimes` lists the identifiers the package has, `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64` and the like, with a shared library for the JIT and a static one for NativeAOT, relative to the package's directory. Either may be missing: a package without a static library cannot be linked into a NativeAOT executable for that identifier, one without a shared library cannot run under the JIT there.
- `symbols` lists the functions the program calls in the package. A kernel is a leaf function by default: it calls nothing back in the program and returns soon, so a NativeAOT executable calls it without .NET's GC transition (`SuppressGCTransition`). `"leaf": false` keeps the transition, for a function that blocks or runs long. Under the JIT every kernel keeps the transition, because a call without it binds the library when the JIT compiles its caller, before the program could choose its C path. `"optional": true` marks a function that returns nothing and that the program calls whatever the CPU, such as one that clears MMX state after other kernels (ffmpeg's `ff_emms_asm`): where the host has no library, a call does nothing.
- `data` (optional) lists the variables the kernels and the program share ([below](#shared-data)).

## Building a package

The project's build assembles its kernels with its native tools for each identifier (`nasm -f elf64`, `-f win64`, `-f macho64`; `clang --target=aarch64-linux-gnu -c kernel.S`), and [scripts/kernels.py](../../scripts/kernels.py) makes the package from the objects: for each identifier a shared library (linked by lld: `ld.lld`, `lld-link` or `ld64.lld`) and a static library, and the manifest with the functions the objects define and the data they share with the program.

```sh
nasm -f elf64 kernel.asm -o x64/kernel.o
nasm -f win64 kernel.asm -o win64/kernel.obj
clang --target=aarch64-linux-gnu -c kernel.S -o arm64/kernel.o
llvmnet -O2 -c dsp.c -o dsp.o                             # the program's C, which defines the tables the kernels read
scripts/kernels.py --name dsp --version 1.0 --library dsp-kernels --output dsp/ \
    --runtime linux-x64 x64/kernel.o --runtime win-x64 win64/kernel.obj --runtime linux-arm64 arm64/kernel.o \
    --bitcode dsp.o --not-leaf dsp_wait --optional dsp_reset
```

Each identifier's objects follow its own conventions: Windows x64 passes arguments in RCX, RDX, R8, R9 and XMM0 to XMM3 by position and leaves narrow arguments' extension to the callee, macOS names symbols with a leading underscore (the helper's manifest does not). The helper needs `ld.lld`, `lld-link` and `ld64.lld` from LLVM 22 (Debian's `lld-22`), and `llvm-nm`, `llvm-ar` and `llvm-objcopy`; `--llvmnet` names the compiler when it is not `bin/llvmnet` of the checkout.

An archive in place of the objects contributes its native members. Projects whose build puts the assembled kernels into the same archives as their C (ffmpeg, dav1d, x264) give it those archives: built with llvmnet, they hold the C as bitcode and the assembly as native objects, and `llvmnet --native-kernels` links their bitcode and leaves their native members to the package. Without `--native-kernels`, a native member is an error that names it. `--exclude` leaves functions the objects define to the program: CPU detection written in assembly, for example, which the program needs in C to run on every host. A function the objects call but neither they nor the program's data define is an error, as kernels must not call back into the program.

## Shared data

Assembly reads tables the C defines (ffmpeg's `ff_pw_*` constants, its run-time-initialized transform tables), and C reads tables the assembly defines. Both must see one copy, so a package's library holds the storage of every shared variable, and the program uses it there:

- `data` in the manifest names the variables. For each, the library exports an accessor, `void *llvmnet_kernel_data_<name>(void)`, which returns its address.
- A variable the program defines lives in the library's storage where the running host has the library: the image's initializer copies the C initializer there, and the program's code and the kernels then read and write the same bytes. Where the host has no library, the program allocates it itself, as without a package.
- A variable only the kernels define is the library's. Where the host has no library, the program gets zeroed storage of its declared size, which its C paths should not read.

`scripts/kernels.py` finds the shared data in the objects' symbol tables (what they reference but do not define, and the data they define) and gets the size and alignment of the program's variables from its bitcode (`--bitcode`), through `llvmnet --print-kernel-data`, which writes the storage and the accessors as C for the library.

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

## Example: ffmpeg's x86 assembly

`FFMPEG_X86ASM=1 LLVMNET_RUNTIME=portable scripts/build-ffmpeg.sh` builds ffmpeg's browser profile with its nasm kernels: configure's `--enable-x86asm` assembles them into the libraries' archives beside the C bitcode, `scripts/kernels.py` makes the `ffmpeg-x86` package from those archives (`artifacts/ffmpeg-browser-portable-x86asm/kernels`), and the decode harness links them with `--native-kernels`. ffmpeg's own CPU detection selects the kernels; its `cpuid.asm` is replaced by C ([config/ffmpeg-x86-cpuid.c](../../config/ffmpeg-x86-cpuid.c), excluded from the package with `--exclude`), so that on an ARM64 host it reports nothing and ffmpeg runs its C functions, and `ff_emms_asm` is optional. As NativeAOT the decode is 1.6 to 1.9 times as fast as without the kernels, and takes about 1.35 times as long as the same profile built natively with its assembly ([evidence](../evidence/2026-11-04-native-kernels.md#ffmpeg-with-its-nasm-kernels)). A NativeAOT build of the harness from its kept bitcode needs the package again:

```sh
bin/llvmnet --runtime=portable --nativeaot --native-kernels artifacts/ffmpeg-browser-portable-x86asm/kernels \
    artifacts/ffmpeg-browser-portable-x86asm/decode.bc -o decode-aot
```
