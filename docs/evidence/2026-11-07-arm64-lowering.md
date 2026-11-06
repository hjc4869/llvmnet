# Evidence: ARM64 forms of the vector lowering for x264 (SV-34 to SV-39, 2026-11-07)

Host `mainmac`: Apple M6 (12 cores), macOS 27.2, .NET 10.0.14 (SDK 10.0.403, ILCompiler for `osx-arm64`). Base commit `22a0f55` on branch `x264-perf`. The workload is the one of the [x86 evaluation](2026-11-06-x264.md): SPEC CPU2017 525.x264_r's first refrate invocation (`--pass 1 --bitrate 1000 --frames 1000`), whose Vector128 build ran 1.93 times native NEON on this Mac (11.41 s against 5.90). A development measurement of one SPEC workload, not a SPEC score; the scripts are in the gitignored `artifacts/arm64/` of the worktree.

## Method

The x86 evaluation's portable bitcode objects (`-O3`, the vectorizers' x86-64 cost model) are relinked with each compiler. The ARM64 code is read from RyuJIT's own listing: the program is linked as a JIT image and run for three frames on the Mac with `DOTNET_TieredCompilation=0` and `DOTNET_JitDisasm` naming the hot functions, which gives NativeAOT's instruction selection in under a second per change (counts below are instructions per function, without alignment padding). Timings are NativeAOT: each compiler writes an `--aot-project`, published on the Mac with `dotnet publish -r osx-arm64`, then timed by the x86 evaluation's `run-mac.py`: median of three, round-robin with native clang-22 NEON, every run from a fresh copy of the executable. macOS has no CPU affinity; the Mac was otherwise idle. Every build of every step wrote native's `BuckBunny_New.264` (md5 `225bcd8b…`) and `x264_stats.log` (`15c045d5…`).

x64 must keep its code or get better. Where the ARM64 form is a branch on `AdvSimd.IsSupported` (a constant for the JIT and for ILC), x64 compiles what it compiled before; the check is x264's NativeAOT image for `linux-x64` (`--aot-debug`), whose `__managedcode` section disassembles to the same text before and after.

## SV-34: narrow vector loads and stores as one instruction

An 8-byte vector load (`load <8 x i8>`, a row of 8 pixels) was `Vector128.CreateScalar(ulong)`, which RyuJIT compiles on ARM64 to `movi v, #0`, `ldr x` and `ins v.d[0], x`; a 4-byte one to `movi`, `ldr w`, `ins v.s[0]`; the stores of 8 and 4 bytes moved the lanes to a general register first (`umov` and `str`). Native code is one `ldr d` or `ldr s`, which zeroes the rest of the register, and one `str d` or `str s`. A probe of the candidate forms on the M6 (RyuJIT's listing):

| Form | ARM64 |
| --- | --- |
| `Vector128.CreateScalar(ReadUnaligned<ulong>)` (before) | `movi`, `ldr x`, `ins v.d[0]` |
| `AdvSimd.LoadVector64(p).ToVector128()` | `ldr d`, `mov v.8b, v.8b` |
| `AdvSimd.LoadVector64(p).ToVector128Unsafe()` | `ldr d` |
| `AdvSimd.LoadAndInsertScalar(Zero, 0, p)` | `movi`, `ld1 {v.d}[0]` |
| `Vector128.CreateScalar(ReadUnaligned<float>)` | `movi`, `ldr s`, `ins v.s[0], v.s[0]` |
| `Vector128.CreateScalarUnsafe(ReadUnaligned<float>)` | `ldr s` |
| `ReadUnaligned<ulong>` store of `ToScalar()` (before) | `umov x`, `str x` |
| `AdvSimd.Store(p, value.GetLower())` | `str d` |
| `WriteUnaligned(p, value.AsSingle().ToScalar())` | `str s` |

`Vectors.Load` and `Vectors.Store` now take the ARM64 forms behind `AdvSimd.IsSupported`: 8 bytes `ldr d` and `str d`, 4 bytes `ldr s` and `str s`, 12 bytes `ldr d` with `ld1 {v.s}[2]` and `str d` with `st1 {v.s}[2]`. The `Unsafe` conversions keep the register as the load left it, zero above, which the lowering's padding rule needs (`Vectors.Load` promises zeros above the bytes read, and SV-26 leaves a narrow load's zero padding out); the float view only moves bits, no arithmetic touches them. 2 and 1 bytes keep their form.

| Function (ARM64 instructions) | before | after |
| --- | ---: | ---: |
| `x264_pixel_satd_8x4` | 201 | 169 |
| `x264_pixel_sad_8x8` | 115 | 81 |
| `x264_pixel_sad_x4_8x8` | 451 | 313 |
| `quant_4x4` | 188 | 164 |
| `x264_pixel_ssd_8x8` | 209 | 177 |
| `hpel_filter` | 808 | 769 |

x264 takes 10.38 s instead of 11.18 (native 5.76 in the same rounds, 1.80 times instead of 1.94). x64's code is unchanged.

**Correctness.** The new fixture [tests/c/vector-pixels.c](../../tests/c/vector-pixels.c) has rows of 4, 8 and 16 bytes loaded, computed and stored, row sums of absolute differences, widened differences and sums, a quantization, a Hadamard transform of 16-bit halves and constant shuffles of one and two sources; [tests/vector-sweep.sh](../../tests/vector-sweep.sh) compares it with native clang in both modes at `-O3` and `-march=x86-64-v3`, as NativeAOT and without hardware intrinsics, and the xp bundle runs it on macOS ARM64 and Linux ARM64. On the M6 it, `vector-reductions.c` and `vector-extensions.c` print native's output under the JIT with and without hardware intrinsics.
