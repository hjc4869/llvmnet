# SIMD128 support

WebAssembly's fixed 128-bit SIMD operations map onto .NET's architecture-neutral `System.Runtime.Intrinsics.Vector128<T>` APIs and compositions of them. .NET uses hardware SIMD instructions where the CPU has them and a software implementation otherwise. The storage contract is `Vector128<T>`, not `System.Numerics.Vector<T>`, whose lane count depends on the host.

The opt-in `--simd128` option compiles code written against WebAssembly's `wasm_simd128.h` intrinsics, in both runtime modes and without changing their 64-bit data ABI. It supports the subset of intrinsics that ffmpeg's WebAssembly HEVC kernels use. Exactly 128-bit vector values become `Vector128<byte>`, and each intrinsic becomes a call to a managed helper. Other vector arithmetic is scalarized as usual.

The ffmpeg profile `FFMPEG_SIMD128=1` enables the HEVC IDCT at 8 and 10 bits and SAO at 8 bits. With the default baseline instruction set, NativeAOT code for these kernels is slower than the scalar build; host-targeted NativeAOT and the JIT are faster (PF-08). [video-simd128-performance.md](video-simd128-performance.md) describes how to measure this.

## Build ffmpeg

```sh
LLVMNET_RUNTIME=system bash scripts/build-dependencies.sh
LLVMNET_RUNTIME=system FFMPEG_SIMD128=1 bash scripts/build-ffmpeg.sh
LLVMNET_RUNTIME=portable bash scripts/build-dependencies.sh
LLVMNET_RUNTIME=portable FFMPEG_SIMD128=1 bash scripts/build-ffmpeg.sh
dotnet artifacts/ffmpeg-browser-portable-simd128/decode.dll input.mp4 100 video 4 frame
```

SIMD build directories have a `-simd128` suffix and share the dependency builds of their runtime mode. The profile configures ffmpeg with `--arch=wasm --enable-asm --enable-simd128` and keeps inline assembly, x86 assembly, hardware decoders and networking disabled. ffmpeg's `ARCH_WASM` selects its C SIMD DSP kernels, while LLVM targets llvmnet's 64-bit ABI. For the portable build, a patched copy of ffmpeg's `configure` ([config/ffmpeg-configure.patch](../config/ffmpeg-configure.patch)) checks `posix_memalign` through `stdlib.h`.

The header is [sysroot/simd128/include/wasm_simd128.h](../sysroot/simd128/include/wasm_simd128.h), selected by `--simd128` in both modes; the helpers are in [src/LlvmNet.Runtime/Simd128.cs](../src/LlvmNet.Runtime/Simd128.cs). Partial 64-bit loads and stores access exactly eight bytes, and the header checks shuffle and lane immediates. Intrinsics the header does not provide are compile errors.

NativeAOT can target the build host's CPU explicitly:

```sh
bin/llvmnet --runtime=portable --simd128 --nativeaot --aot-instruction-set native \
	artifacts/ffmpeg-browser-portable-simd128/decode.bc \
	-o artifacts/ffmpeg-browser-portable-simd128/decode-aot-host
```

The default is `--aot-instruction-set baseline`. `native` specializes the code for the build host's CPU and rejects another RID; the result may not run on older CPUs. It does not change the C data ABI. The JIT selects instructions at run time without this option.

## Tests

```sh
bash tests/wasm-simd128.sh
bash tests/simd128.sh
FFMPEG_SIMD128_SKIP_BUILD=1 bash tests/ffmpeg-simd128.sh
```

The tests need Clang 22 with the WebAssembly target, a `wasm-ld` executable (`WASM_LD` overrides discovery), Node.js with SIMD support, and .NET 10 with the NativeAOT publishing tools. `tests/ffmpeg-simd128.sh` also needs a host ffmpeg with libx265 to generate its fixtures.

- [tests/wasm-simd128.sh](../tests/wasm-simd128.sh) compiles [tests/wasm/simd128.c](../tests/wasm/simd128.c) with Clang's real WebAssembly intrinsics and runs it in Node as the oracle. [tests/LlvmNet.Checks/Simd128Probe.cs](../tests/LlvmNet.Checks/Simd128Probe.cs) evaluates the same 21 operations on the same 67 input records (three boundary cases and 64 deterministic random ones) with `Vector128<T>`. Every result must match under the default JIT, with `DOTNET_EnableHWIntrinsic=0` (which must report no hardware acceleration) and under NativeAOT.
- [tests/simd128.sh](../tests/simd128.sh) compiles the same C fixture with llvmnet's header in both modes and compares the output with the WebAssembly oracle under the JIT, the software fallback, baseline NativeAOT and host-targeted NativeAOT.
- [tests/ffmpeg-simd128.sh](../tests/ffmpeg-simd128.sh) checks that ffmpeg dispatches to the SIMD kernels and compares 512 IDCT and 144 SAO cases with scalar ffmpeg, including output-buffer boundaries. It decodes 8- and 10-bit HEVC fixtures with frame and slice threading in both modes under the JIT, the software fallback and NativeAOT, compares the frame hashes with the native decoder, and requires the portable decoder to have no P/Invoke imports.

## Operation mappings

| WebAssembly operation | .NET mapping | Semantic requirement |
| --- | --- | --- |
| unaligned load, wrapping add/multiply | `LoadUnsafe`, vector arithmetic | Preserve all 128 bits and lane-width wrapping. |
| byte left shift, signed short right shift | `ShiftLeft`, `ShiftRightArithmetic` | Mask the count by lane width minus one. |
| byte swizzle | `Vector128.Shuffle` | Out-of-range indices produce zero, not modulo-16 selection. |
| two-source byte shuffle | Two `Shuffle` operations and OR | Second-source indices subtract 16; validate immediates in 0..31. |
| bitselect, bitmask, all/any true | `ConditionalSelect`, `ExtractMostSignificantBits`, zero comparisons | Bitselect takes the mask first in .NET; all-true means each lane is nonzero, not all bits set. |
| unsigned byte saturated add | Widen, add, clamp, narrow | Clamp before narrowing. Ordinary `Narrow` truncates. |
| signed/unsigned saturated narrowing | Signed clamp followed by `Narrow` | Both wasm variants interpret input lanes as signed; unsigned output clamps negatives to zero. |
| signed low-half extension | `WidenLower` | Select signed or unsigned source lane interpretation explicitly. |
| signed Q15 rounded saturated multiply | Widen, multiply, add `0x4000`, arithmetic shift, clamp, narrow | The `-32768 * -32768` case saturates to 32767. |
| float minimum/maximum | `Vector128.Min` / `Max` | Propagate NaNs and preserve the specified signed-zero result. Avoid `MinNative` / `MaxNative`. |
| float pseudo-minimum | Comparison plus `ConditionalSelect` | Exactly `right < left ? right : left`, including NaNs and signed zero. |
| float to signed/unsigned integer saturation | `ConvertToInt32` / `ConvertToUInt32` | .NET 10 non-`Native` conversions produce zero for NaN and saturate overflow. |

The min/max comparison in the tests canonicalizes NaNs on both sides, because WebAssembly arithmetic permits NaN payload and sign variation; it still compares non-NaN values and signed zeros bit for bit. Pseudo-minimum is compared without that normalization. The tests include infinities, out-of-range float conversions, negative-to-unsigned conversion, narrowing limits, arbitrary bitselect masks, out-of-range swizzles and unaligned loads.

The ffmpeg kernel tests also exercise signed short extended multiplication, saturating short addition, byte widening, unsigned byte comparisons, short/int/long shuffles and partial 64-bit memory operations. Pairwise dot products are not `Vector128.Dot`, which reduces to a scalar. Pairwise extended add, byte population count, f64 conversions, the other partial-width memory operations and the remaining lane types are not implemented. Relaxed SIMD would need its own rules for the results it allows. WebAssembly memory bounds and traps do not apply: the header works on ordinary C pointers.

## Compiler integration

- The header produces recognized `__llvmnet_simd128_*` helper calls that survive scalarization. With `--simd128`, the optimizer runs LLVM's scalarizer without load/store expansion, so 128-bit vector loads and stores stay whole and map to `Vector128<byte>`. Other fixed-width vectors keep their explicit-layout value types.
- Source compatibility is separate from the machine ABI. Clang's own `wasm_simd128.h` requires a WebAssembly target; llvmnet's header works under both 64-bit ABIs. wasm32 pointers, layouts, varargs and library contracts are incompatible with them, and WebAssembly modules cannot be loaded.
- SIMD helpers are managed imports in both modes. ffmpeg's DSP entry points take scalar pointer arguments, so their vector values stay in managed code. Passing vector values to or from native code in system mode is not supported.
- Open work: direct acceleration of generic LLVM vector instructions and of target-specific `llvm.wasm.*` intrinsics, kernels beyond HEVC IDCT and 8-bit SAO (10-bit SAO, motion compensation and deblocking are scalar), ARM64 execution, and baseline NativeAOT code quality.

## References

- [WebAssembly SIMD128 operation semantics](https://github.com/WebAssembly/simd/blob/main/proposals/simd/SIMD.md)
- [.NET Vector128 API](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.vector128?view=net-10.0)
- [Compiler architecture](architecture.md)
