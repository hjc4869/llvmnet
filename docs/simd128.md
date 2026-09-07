# SIMD128 Support

Yes: WebAssembly's fixed 128-bit operations can be implemented with architecture-neutral `System.Runtime.Intrinsics.Vector128<T>` APIs and compositions on the project's .NET 10 baseline. These APIs are not the architecture-specific `X86.Sse2` or `Arm.AdvSimd` APIs. Hardware acceleration is selected by .NET where available; the same operations have software behavior. Do not use `System.Numerics.Vector<T>` as the storage contract because its lane count depends on the host.

The opt-in `--simd128` compiler path now supports the intrinsic subset used by ffmpeg's upstream WebAssembly HEVC kernels through an owned compatibility header. It preserves the existing 64-bit system/portable ABI and emits `Vector128<byte>` values plus architecture-neutral managed helper calls. This is not a wasm binary loader or complete SIMD128 implementation. Ordinary auto-vectorized arithmetic still uses the scalar fallback; the compatibility header preserves the selected operations as explicit SIMD helpers.

The `FFMPEG_SIMD128=1` build profile enables HEVC IDCT at 8/10 bits and SAO at 8 bits. All 2,829 decoded frames in the six supplied `2026*.mp4` files matched installed native ffmpeg in both SIMD ABIs. JIT throughput improved about 13% in aggregate; host-targeted NativeAOT improved about 15%. Default-baseline NativeAOT SIMD was slower and is not recommended for performance on this host. See [video-simd128-performance.md](video-simd128-performance.md) for full measurements and limitations.

## Build ffmpeg

```sh
LLVMNET_RUNTIME=system bash scripts/build-dependencies.sh
LLVMNET_RUNTIME=system FFMPEG_SIMD128=1 bash scripts/build-ffmpeg.sh
LLVMNET_RUNTIME=portable bash scripts/build-dependencies.sh
LLVMNET_RUNTIME=portable FFMPEG_SIMD128=1 bash scripts/build-ffmpeg.sh
dotnet artifacts/ffmpeg-browser-portable-simd128/decode.dll input.mp4 100 video 4 frame
```

SIMD build directories have a `-simd128` suffix; scalar directories and dependency ABI tags are unchanged. ffmpeg's `ARCH_WASM` selects its C SIMD DSP kernels, not a wasm machine ABI: LLVM still targets the existing 64-bit ABI. The profile enables ffmpeg's architecture-dispatch configure switch but keeps inline assembly, x86 assembly, hardware decoders, and network support disabled. A build-local configure patch makes the portable aligned-allocation probe use the actual `posix_memalign` header/signature. No upstream source checkout is edited.

The owned header is [../sysroot/dotnet64-v1/simd128/include/wasm_simd128.h](../sysroot/dotnet64-v1/simd128/include/wasm_simd128.h), selected by `--simd128` in both ABIs. Implementations are in [../src/LlvmNet.Runtime/Simd128.cs](../src/LlvmNet.Runtime/Simd128.cs). Partial 64-bit loads/stores access exactly eight bytes; shuffle/lane immediates are checked by the header. Unsupported header operations remain compile errors rather than silently using native SIMD libraries.

For this machine only, host-targeted NativeAOT is available explicitly:

```sh
bin/llvmnet --runtime=portable --simd128 --nativeaot --aot-instruction-set native \
	artifacts/ffmpeg-browser-portable-simd128/decode.bc \
	-o artifacts/ffmpeg-browser-portable-simd128/decode-aot-host
```

The default is `--aot-instruction-set baseline`. `native` specializes for the build host CPU, rejects another RID, and can produce a binary that will not run on older CPUs. It does not change the portable C data ABI. The JIT selects supported instructions at execution time without this publishing option.

## Reproduce

```sh
bash tests/wasm-simd128.sh
bash tests/simd128.sh
FFMPEG_SIMD128_SKIP_BUILD=1 bash tests/ffmpeg-simd128.sh
```

Requires Clang 22's WebAssembly frontend/header, a `wasm-ld` executable, Node.js with SIMD support, and .NET 10 with NativeAOT publishing tools. `WASM_LD` overrides linker discovery. The checked environment used Clang 22.1.8, wasm-ld 21, Node.js 24.19.0, and .NET runtime 10.0.11 on Linux x86-64.

[../tests/wasm/simd128.c](../tests/wasm/simd128.c) uses real Clang WebAssembly intrinsics; Node executes the resulting module as the oracle. [../tests/LlvmNet.Checks/Simd128Probe.cs](../tests/LlvmNet.Checks/Simd128Probe.cs) evaluates the same input records through `Vector128<T>`. There are 21 operations per record and 67 records: three boundary/zero inputs plus 64 deterministic randomized inputs. All 1,407 vector results matched under default .NET JIT, `DOTNET_EnableHWIntrinsic=0`, and NativeAOT. The test asserts that the forced software run reports no hardware acceleration. Artifacts are under `artifacts/tests/wasm-simd128/`.

[../tests/simd128.sh](../tests/simd128.sh) additionally compiles that same C fixture with llvmnet's header and compares generated system/portable CIL with the real wasm oracle under JIT, forced software fallback, baseline NativeAOT, and host-targeted NativeAOT. [../tests/ffmpeg-simd128.sh](../tests/ffmpeg-simd128.sh) asserts actual DSP dispatch and compares 512 IDCT and 144 SAO cases with scalar ffmpeg, including output-buffer boundaries. Real 8/10-bit HEVC fixtures pass hash and decode-only checks with frame/slice threading in both ABIs under JIT, software fallback, and NativeAOT. Portable SIMD decode output has 7,216 IL method bodies and zero generated P/Invoke imports. JIT disassembly of the actual IDCT kernel contains packed SIMD instructions.

## Tested Mappings

| WebAssembly Operation | .NET 10 Mapping | Semantic Requirement |
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

The min/max comparison canonicalizes NaNs on both sides because WebAssembly arithmetic permits NaN payload/sign variation; it still compares non-NaN values and signed zeros bit-for-bit. Pseudo-minimum is compared without that normalization. Tests include infinities, out-of-range float conversions, negative-to-unsigned conversion, narrowing limits, arbitrary bitselect masks, out-of-range swizzles, and unaligned loads.

Other SIMD128 operations are plausible direct mappings or compositions, not yet verified by the original probe. The ffmpeg kernel tests additionally exercise signed short extended multiplication, saturating short addition, byte widening, unsigned byte comparisons, short/int/long shuffles, and partial 64-bit memory operations. Pairwise dot products are not `Vector128.Dot`, which reduces to a scalar. Pairwise extended add, byte population count, f64 conversions, remaining partial-width memory operations, and all lane types still need implementation and differential cases. Relaxed SIMD requires its own allowed-result rules; it must not silently weaken strict SIMD operations. A genuine wasm memory implementation would also require bounds/trap semantics, which this unsafe C ABI does not provide.

## Compiler Integration

1. Implemented: the compatibility header produces recognized `__llvmnet_simd128_*` helper calls that survive scalarization. With `--simd128`, exactly 128-bit vector values map to `Vector128<byte>` and vector loads/stores are preserved. Keeping `scalarizer<load-store>` initially caused severe lane-materialization overhead; using `scalarizer` without load/store expansion removed it. Other vector arithmetic retains the scalar fallback and other fixed-width storage retains explicit-layout values.
2. Still open: direct acceleration of generic LLVM vector instructions and target-specific `llvm.wasm.*` intrinsics. Clang's wasm probe IR includes generic saturation/min/max/conversion intrinsics as well as wasm shuffle, swizzle, bitselect, bitmask, narrowing, Q15 multiplication, and pseudo-minimum. The current header route handles its declared subset, not arbitrary precompiled wasm IR.
3. Implemented: source compatibility is separate from the machine ABI. The stock Clang wasm header requires a WebAssembly target; llvmnet's owned header works under both existing 64-bit ABIs. wasm32 pointers, layouts, varargs, and library contracts are incompatible and remain rejected. Actual WebAssembly module support is separate work.
4. Native boundaries remain explicit. SIMD helpers are managed imports even in system mode, and unsupported vector-valued unmanaged callbacks remain errors. The ffmpeg DSP entry points themselves use scalar pointer arguments; their internal vector calls and data stay managed. General native vector callback/library interoperability is not claimed.
5. Expand beyond HEVC IDCT/8-bit SAO and the tested helper subset. The supplied 10-bit videos exercise IDCT acceleration only; motion compensation, deblocking, and 10-bit SAO are still scalar. Cross-platform/ARM64 execution, exhaustive intrinsic coverage, SIMD library-boundary tests, baseline NativeAOT vector-code quality, and broader FATE coverage remain open.

## References

- [WebAssembly SIMD128 operation semantics](https://github.com/WebAssembly/simd/blob/main/proposals/simd/SIMD.md).
- [.NET Vector128 API](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.vector128?view=net-10.0).
- [Current compiler architecture](architecture.md).