# Evidence: SV-13, native kernel packages (2026-11-04)

Host: AMD Ryzen AI MAX+ 395, Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `d37f2ce`. Other hosts as in the [SV-12 evidence](2026-11-04-cpu-detection.md). Design: [12-simd.md](../plan/12-simd.md#level-3-native-kernel-packages); user documentation: [native-kernels.md](../user/native-kernels.md).

## The mechanism

`--native-kernels <dir>` reads a package's `manifest.json` (name, version, library name, per runtime identifier a shared and a static library, the symbols with their leaf flag). Every function the program declares and the package lists gets, in the image, a wrapper with the declaration's managed signature and two P/Invoke methods of the native signature (`int` for integers up to 32 bits, `long`, `nint`, `float`, `double`). The wrapper extends narrow arguments as their `signext` or `zeroext` attribute says, calls the P/Invoke that `RuntimeFeature.IsDynamicCodeCompiled` selects (a constant to the JIT and to ILC) and keeps the low bits of a narrow result:

- Under the JIT, the P/Invoke's library name is `llvmnet-kernel:<package>:<symbol>`, and it binds at its first call. The image's initializer registers the package with `NativeKernels.Register`, whose `DllImportResolver` then loads `runtimes/<rid>/native/<library>` from beside the image (llvmnet copies every identifier's shared library there) or ends the process with a line naming the kernel, the package and the identifiers it has.
- As NativeAOT, the P/Invoke names the package's library, `SuppressGCTransition` for leaf kernels, and the generated project links each identifier's static library with `DirectPInvoke` for that library, both conditioned on `$(RuntimeIdentifier)`.

The JIT's P/Invoke keeps the GC transition because a P/Invoke without it has no lazy binding stub: RyuJIT binds it whenever it compiles a method that calls it. With `SuppressGCTransition` under the JIT, compiling `main`, which merely could call a kernel, ran the resolver and ended the process on a host without the library, and `RuntimeHelpers.PrepareMethod`, which `tests/jit-validation.sh` calls on every method, failed on an image whose library was not beside it. Unoptimized code (tier 0) also keeps the NativeAOT branch of the constant test, whose transition-free P/Invoke the JIT then bound; the wrapper is therefore `AggressiveOptimization` as well as `AggressiveInlining`. A function pointer to a kernel is the wrapper's, so DSP tables work as they do for C functions. `llvmnet_native_kernels(name)` (`<llvmnet/cpu.h>`) answers whether the running host has the package's library. A variadic kernel, or one with a `byval`, `sret` or similar parameter, is a compile error naming it; the image lists its packages in `llvmnet.kernels`.

## The synthetic package

[tests/kernels/synthetic](../../tests/kernels/synthetic) has three kernels in x86-64 and AArch64 assembly: an SSE2 or NEON `int32` add, a sequential `double` dot product and `int16_t sk_mix(int8_t, uint16_t, int64_t, float)`, whose x86-64 version relies on the caller's extension of its narrow arguments, as the System V convention says. The program calls them through a table and directly, with C fallbacks it selects by `llvmnet_native_kernels`; [tests/native-kernels.sh](../../tests/native-kernels.sh) assembles the package with Clang and GNU ld (the AArch64 library when the cross linker is installed).

| host and engine | library | result |
| --- | --- | --- |
| Linux x64, JIT | linux-x64 shared library, loaded at the first kernel call | as native linked with the kernels |
| Linux x64, NativeAOT | linux-x64 static library, no dynamic dependency | as native |
| Linux x64, JIT, library removed | none | the C paths, as native; with the argument "kernels" exit 127 and llvmnet's message |
| Linux ARM64 (qemu), JIT | linux-arm64 shared library | as native |
| macOS ARM64, JIT | none (the package has no osx-arm64 library) | the C paths, as native; with "kernels" exit 127, naming `sk_add_i32`, the package and its linux-x64 and linux-arm64 |

## The cost of a call

`sk_mix` through a volatile function pointer, 100 million calls, median seconds of five runs; per call after the process start (0.03 s under the JIT):

| | seconds | per call |
| --- | ---: | ---: |
| native | 0.110 | 1.1 ns |
| JIT (with the GC transition) | 0.511 | 4.8 ns |
| NativeAOT, leaf (`SuppressGCTransition`) | 0.273 | 2.7 ns |
| NativeAOT, `"leaf": false` | 0.447 | 4.4 ns |

Without the transition the JIT's call cost 2.9 ns in a first version that bound at compile time, as measured before it was dropped for the reason above.

## What remains

The real case of the item, ffmpeg's browser profile with `--enable-x86asm` (185 nasm files) through a package, needs nasm, which this host does not have; packages for `win-x64` and `osx-arm64` need lld (`lld-link`, `ld64.lld`), which it does not have either. The xp bundle does not carry a package yet. System mode calls native libraries through `--system-library`; its kernel objects and archives are SV-15's.
