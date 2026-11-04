# Evidence: SV-15, native objects and assembly in system mode (2026-11-04)

Host: AMD Ryzen AI MAX+ 395, Linux 6.12.113, .NET 10.0.14, clang 22.1.8, nasm 3.01; base commit `a85240d`. Design: [12-simd.md](../plan/12-simd.md#level-3-native-kernel-packages); user documentation: [linking.md](../user/linking.md#native-objects-and-assembly-system-mode).

## The mechanism

A system-mode link now takes a project's assembled code:

- **Inputs.** Native object files on the command line, recognized by their content (ELF relocatable, COFF, Mach-O), the native members of archives that also hold bitcode (the linker skips them in system mode, and `native/objects.cpp` extracts them under unique names, as one archive can hold two members of one name), and assembly sources: `.s`, `.S`, `-x assembler` and `assembler-with-cpp` through Clang for x86-64 Linux with the command line's options, `.asm` through nasm (`-f elf64`, its `-I` and `-D` options). `-c` on an assembly source writes the native object.
- **Symbols.** `llvmnet_native_objects` lists each object's defined functions (symbols in executable sections), defined data and undefined symbols. Every symbol the objects reference is a required root of the bitcode link and stays external (`--internalize-public-api-list`), so a table only the assembly reads is linked and kept.
- **Calls.** A function the objects define resolves to the image's native library (`lib<name>.llvmnet-host.so`, or the static archive NativeAOT links). When its parameters and result are 32- or 64-bit integers, pointers, `float` or `double`, the library gets a forwarder, `llvmnet_object_<name>`, a `musttail` call that reaches a symbol the assembler kept hidden, and translated code calls the forwarder through an inlined wrapper and a P/Invoke without the GC transition, as kernel packages do. Other signatures (narrow integers, structures, variadic functions) use the bridges of system libraries' functions.
- **Data.** As for kernel packages ([SV-13](2026-11-04-native-kernels.md#shared-data)): a variable the program defines and the objects read, and a variable the objects define and the program uses, live in the native library, whose `llvmnet_kernel_data_<name>` accessors the image's initializer calls, copying the C initializer in. The library is linked with `-Bsymbolic`, as assembly addresses its own data relative to the instruction.
- **Errors.** An object that calls a function of the program fails the compile naming the object (`callback.S`, `cbmix.a(callback.o)`) and the function; a symbol that neither the objects, the program nor a system library defines fails naming it. Portable mode rejects assembly sources and native objects, naming them and pointing to kernel packages, except that a link with `--native-kernels` leaves native objects to the package.
- **Inline assembly** (BE-24): the rejection now names the switches that leave it out of common projects (ffmpeg's `--disable-inline-asm`, OpenSSL's `no-asm`, libjpeg-turbo's `-DWITH_SIMD=0`) and where assembly goes instead; the support matrix and the limitations page are regenerated.

[tests/system-objects.sh](../../tests/system-objects.sh) runs the synthetic kernels' x86-64 assembly (with the data they share with the program both ways) given as a `.S` source, as an object from `llvmnet -c`, and as an archive member beside the program's bitcode, under the JIT and as NativeAOT (statically linked, no dynamic dependency on the native library), and a nasm kernel with a `-D` value; each prints what the native program prints. It checks the four errors above. jit-validation found that one process resolves a second image's `lib<name>.llvmnet-host.so` to the first one it loaded when the images have the same name: the initializer's accessor call then failed with a missing entry point, so the test's images have distinct names.

## ffmpeg with its nasm kernels in system mode

`FFMPEG_X86ASM=1 LLVMNET_RUNTIME=system scripts/build-ffmpeg.sh` configures the browser profile with `--enable-x86asm` (72 s with 24 jobs, the link included), and the decode harness links the archives' 91 nasm objects into its native library (2.2 MB): 1,782 kernels through forwarders, 213 calls through bridges (the accessors, signatures with narrow integers, the C library). System mode needs neither the C CPU detection nor the optional `ff_emms_asm` of the portable profile, as `cpuid.asm` and `emms` run natively; it keeps `HAVE_EBX_AVAILABLE 0`. A NativeAOT build from the kept `decode.bc` names the archives again, which contribute their native members. Every frame's MD5 of the 240-frame 1080p HEVC 10-bit and H.264 clips equals native's, under the JIT and as NativeAOT.

Median seconds of five runs in alternating order (`scripts/ab-time.py`; new processes, so the JIT columns include startup and compilation); `native asm` is the same profile built natively with Clang and nasm, as in the [SV-13 evidence](2026-11-04-native-kernels.md#ffmpeg-with-its-nasm-kernels):

| clip, threads | native asm | system kernels, JIT | system kernels, NativeAOT | system scalar, JIT | system scalar, NativeAOT | portable kernels, NativeAOT |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| HEVC 1080p 10-bit, 1 | 1.090 | 1.923 | 1.437 | 2.894 | 2.387 | 1.441 |
| HEVC 1080p 10-bit, 4 | 0.475 | 1.080 | 0.627 | 1.348 | 0.936 | 0.608 |
| H.264 1080p, 1 | 0.632 | 1.789 | 0.946 | 2.472 | 1.649 | 0.889 |
| H.264 1080p, 4 | 0.208 | 0.885 | 0.324 | 1.082 | 0.501 | 0.294 |

As NativeAOT the kernels make system mode's decode 1.66 (HEVC) and 1.74 (H.264) times faster than its scalar build on one thread, and it takes 1.32 and 1.50 times as long as native assembly. Before the forwarders every kernel call went through a bridge, which packs the arguments into a buffer and keeps the GC transition: 1.513 and 1.034 s on one thread, against 1.437 and 0.946 now; the portable package's H.264 decode stays 6% faster, which calls with narrow integer parameters through bridges may explain.

## What remains

System mode targets Linux x86-64 only, so its native objects are ELF; Windows and macOS hosts use portable mode's kernel packages. Every native member of an archive is linked, not only those that resolve a reference, which is what ffmpeg's profile needs; a library whose unused members reference missing symbols would need native archive semantics.
