# Evidence: SV-12, CPU detection without assembly (2026-11-04)

Host: AMD Ryzen AI MAX+ 395 (Zen 5, AVX-512), Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `7a3cf6d`. The other hosts are those of the [SV-08 evidence](2026-11-04-x86-intrinsics.md): Windows x64 on an AMD Ryzen 9 9955HX, macOS on an Apple M6, and Linux ARM64 in the arm64 .NET container under qemu. Design: [12-simd.md](../plan/12-simd.md#level-3-native-kernel-packages) ("Fallback and detection"); user documentation: [x86-intrinsics.md](../user/x86-intrinsics.md#cpu-detection).

## What changed

- **CPUID and XGETBV in portable mode.** The backend recognized three spellings of CPUID inline assembly and ran them in native thunks in system mode; portable mode rejected them, and SV-08 found that `--isa=x86-64-v2` makes libraries compile their detection (wuffs, Chromium zlib, astc-encoder's command line). The recognizer now compares the instructions without white space, operand-size suffixes and LLVM's `$(att$|intel$)` alternatives, and checks the constraints by role: plain `cpuid` with EBX as an output, or `cpuid` between a `mov` or `xchg` that keeps RBX in another register (LLVM's and Eigen's form, ffmpeg's macro, Clang 22's `<cpuid.h>`), with the leaf and optionally the subleaf; XGETBV as `xgetbv` or its `.byte` encoding. In portable mode the call writes the registers into the result structure through `CpuFeatures.CpuId` and `XGetBv`: `X86Base.CpuId` on x64, zeros where `X86Base` is not supported (ARM64, or hardware intrinsics off), so leaf 0's maximum of 0 tells a library there is nothing to ask. .NET has no XGETBV, so XCR0 is CPUID leaf 0xD's supported state, which Linux enables in full, without the AVX component where `Avx.IsSupported` is false and without AVX-512's where `Avx512F.IsSupported` is: this host's XCR0 is `0x2e7`, as native code reads it.
- **compiler-rt's CPU model.** `cpu_model/x86.c` is in both sysroots' `libclang_rt.builtins.a` (portable: compiled with `-D__x86_64__`, as the sysroot's other compiler-rt members are; system mode: a new archive beside flang-rt, which system executables link). Its queries are `<cpuid.h>` and the `.byte` XGETBV, so `__cpu_model`, `__cpu_features2` and `__cpu_indicator_init` come from the same CPUID in both modes, and `__builtin_cpu_supports`, `__builtin_cpu_is` and `__builtin_cpu_init` link; before, `__cpu_model` was unresolved in both modes. Its constructor returns `int`, which the constructor runner now drops instead of rejecting.
- **target_clones and ifuncs.** For the portable triple, which is not Linux, Clang emits a `target_clones` function as a dispatcher that tests `__cpu_model` and tail-calls a clone; it needed only the CPU model. Clang ignores a hand-written `ifunc` attribute there (with a warning), so such a function stays unresolved in portable mode. For system mode's Linux triple Clang emits GNU ifuncs, which the linker now lowers when a module has any: LLVM's `lowerGlobalIFuncUsersAsGlobalCtor` gives each a table entry that a priority-10 constructor fills by calling the resolver, before `__cpu_indicator_init` (priority 90) and the program's constructors, as Clang's resolvers call `__cpu_indicator_init` themselves. The table and the constructor, which LLVM leaves unnamed, are named for the translator. The support matrix's ifunc probe now translates.
- **`AT_HWCAP` and `AT_HWCAP2`** join the auxiliary vector portable programs start with: CPUID leaf 1's EDX and HWCAP2_FSGSBASE on x64, as Linux gives them, and the `HWCAP_*` bits of the instruction sets .NET reports on ARM64. musl's `getauxval` returns them. glibc's `getauxval(AT_HWCAP)` returns its own platform bits on x86-64 (2 here), so the test compares with the kernel's vector (`LD_SHOW_AUXV=1`).
- **`<llvmnet/cpu.h>`**: `llvmnet_cpu_features()` with `LLVMNET_CPU_*` bits from the `IsSupported` properties (SSE2 to AVX-512BW, NEON, AES, CRC32, DotProd, RDM, SHA1, SHA256, SVE), and `llvmnet_native_kernels()`, which returns 0 until kernel packages exist (SV-13).

## Tests

[tests/c/cpu-detection.c](../../tests/c/cpu-detection.c) (built for `x86-64-v2`) prints `<cpuid.h>`'s leaves 1 and 7, XCR0, 16 `__builtin_cpu_supports` features, `__builtin_cpu_is("amd")` and `("intel")`, the result of a `target_clones` function and of an ifunc whose resolver tests AVX2. [tests/cpu-detection.sh](../../tests/cpu-detection.sh):

| build | result |
| --- | --- |
| JIT, portable and system mode | as native |
| NativeAOT, system mode | as native |
| NativeAOT, portable, `--aot-instruction-set native` | as native |
| NativeAOT, portable, the default `x86-64-v2` | as the JIT with `DOTNET_EnableAVX=0`: XCR0 `0x203`, no AVX, AVX2 or AVX-512 in the CPU model, the ifunc's SSE target |
| JIT, portable, `DOTNET_EnableHWIntrinsic=0` | no CPUID, no feature, the default clone and the ifunc's SSE target |

A consistency mode prints only what holds on every host (CPUID, the CPU model and `<llvmnet/cpu.h>` agree, and the dispatch follows them); it is in the xp bundle and holds on Linux ARM64, on macOS ARM64 under the JIT and as NativeAOT, and on Windows x64 under the JIT and as NativeAOT, where CPUID reports that host's own Zen 5. `tests/c/cpu-query.c` adds ffmpeg's spelling, the plain `cpuid` and the `xgetbv` mnemonic, each compared with the other forms; its portable build now prints what native code prints, and nothing with hardware intrinsics off. `tests/linkage.sh` runs system mode's ifunc fixture instead of expecting its rejection.

## Real detection code

| code | before | now |
| --- | --- | --- |
| ffmpeg's `ff_get_cpu_flags_x86` (`libavutil/x86/cpu.c` with inline assembly, no nasm) | portable: rejected | `007fd3db` as native in both modes; `004e13db` (no AVX flags) with AVX off and as baseline NativeAOT; 0 with hardware intrinsics off |
| Chromium zlib's `cpu_features.c` | portable: rejected, replaced by a stub in SV-08's measurement | selects its SSSE3 adler32 and SSE4.2 and PCLMUL crc32 in both modes, at the stub's speed (deflate 0.439 s against 0.436 s); C paths with hardware intrinsics off |
| wuffs (astc-encoder's PNG decoder) | portable: rejected, `WUFFS_CONFIG__AVOID_CPU_ARCH` | its SSE4.2 path, output byte-identical to native |
| astc-encoder's command-line check (`astcenccli_entry.cpp`) | portable: rejected, compiled as for SSE2 | passes; with hardware intrinsics off it stops with "Host does not support SSE4.1 ISA extension", as on a host without SSE4.1 |
| compiler-rt's CPU model | unresolved in both modes | as native |

dav1d's detection (`dav1d_cpu_cpuid`, `dav1d_cpu_xgetbv`) and ffmpeg's nasm build of `cpuid.asm` are assembly files, which need a native kernel package (SV-13). libpng selects its SSE2 and NEON filters at compile time on x86; zlib-ng is not in the pinned sources, and Chromium zlib's `cpu_features.c` reads CPUID through the same `<cpuid.h>`.

## What it does not show

The CPUID leaves are the host's: a library that finds AVX2 and selects code compiled with `target("avx2")` runs it as vector code if it is generic C, and fails at the link if it uses AVX2 intrinsics, which SV-09 translates. Leaf 1's EBX (the APIC id) and the x2APIC leaves describe the core the calling thread happens to run on, as natively.
