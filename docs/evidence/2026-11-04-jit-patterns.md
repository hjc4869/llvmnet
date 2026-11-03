# Evidence: CIL shapes and RyuJIT (2026-11-04)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (Zen 5), .NET runtimes 10.0.14 and 11.0.0-rc.2.26504.105, crossgen2 and ILCompiler 10.0.14, LLVM/Clang 22.1.8. The compiler is `ee9304b`, built in a scratch worktree with the experiment switches below. JIT sources read: dotnet/runtime `main` at `7f4a969` (2026-11-02, after `release/11.0` branched), the sparse checkout in `artifacts/coreclr/runtime` from the CoreCLR review. The ReadyToRun loading check used macOS ARM64 (`mainmac`, .NET 10.0.14) and Windows x64 (`fluorite`).

The owner's question: is RyuJIT tuned for particular patterns of IL, and can llvmnet emit them? The plan that follows is [13-jit-patterns.md](../plan/13-jit-patterns.md) (workstream JP, milestone M12), with [ADR-17](../plan/decisions.md#adr-17-readytorun-for-jit-deployments).

Inputs and tools are in `artifacts/jit-patterns/` (gitignored):

- `experiments.patch`: the `LLVMNET_EXPERIMENT` switches, applied to `ee9304b`.
- `kernels/`: `interp.c`, `globals.c`, `blk.c`.
- `ilstats/`: RyuJIT's MinOpts inputs per method, from an image's metadata.
- `subset.py`: the benchmark subset run.
- `results/`: JIT summaries, statistics and logs.

## What the JIT's sources say

| Area | RyuJIT (file in `src/coreclr/jit` unless noted) | llvmnet's CIL today |
| --- | --- | --- |
| MinOpts | `compSetOptimizationLevel` (`compiler.cpp`): MinOpts above 60,000 IL bytes, 20,000 IL instructions (prefixes not counted), 2,000 blocks, 2,000 locals (`lvaCount`, JIT temporaries included) or 8,000 `ldloc`/`stloc`/`ldarg`/`starg` references. In release builds these are the `DEFAULT_MIN_OPTS_*` constants; the `JITMinOpts*` settings are read only in debug builds. Skipped when `IsAot()` (NativeAOT and crossgen2). | One local, one `stloc` and at least one `ldloc` per LLVM value; long encodings (below). |
| Rejit of a MinOpts method | `TieredCompilationManager::CompileCodeVersion` (`vm/tieredcompilation.cpp`): when the Tier1 compilation switches to MinOpts, the new code is discarded and the method keeps its first code. | Over-limit methods are compiled twice. |
| Tracked locals | `lvaSortByRefCount` (`liveness.cpp`): at most `JitMaxLocalsToTrack` (1,024) locals are tracked; the rest live on the stack. | One local per value, so large functions exceed it (SV-05's sharing, in progress, addresses it). |
| Inlining | `fgInline` (`fginline.cpp`): no inlining into a method with at least 90% of `JitMaxLocalsToTrack` locals (`CALLSITE_TOO_MANY_LOCALS`); struct promotion stops at the limit too (`morph.cpp`). CSE takes at most 64 candidates (`MAX_CSE_CNT`). | Runtime helpers called from large functions are real calls. |
| `static readonly` | `impImportStaticReadOnlyField` (`importer.cpp`) with `CEEInfo::getStaticFieldContent` (`vm/jitinterface.cpp`): in optimized code, an `initonly` static of a class that `IsClassInitedOrPreinited` is read at compile time and becomes a constant. | `__global*` and `__constant*` are plain `static nint` fields written by `Program`'s type initializer: one load per access, which a store through any pointer prevents hoisting (PF-17). |
| `switch` | `optRecognizeAndOptimizeSwitchJumps` (`switchrecognition.cpp`): rebuilds a switch only from `x == c1 \|\| x == c2 ...` chains with one shared target, at least 3 tests (5 on ARM64) within 63 values ("TODO: recognize general jump table patterns"). The CIL `switch` becomes a jump table or a bit test. | Every LLVM `switch` is a chain of `ldloc; ldc; bne.un`. |
| Block copies | `getUnrollThreshold` (`compiler.h`): `cpblk`/`initblk` of a constant size are unrolled up to 128/256 bytes with AVX, 64/128 with SSE only, 128/256 on ARM64. | `llvm.memcpy`/`llvm.memset` call `Memory.Copy`/`Memory.Set` (`NativeMemory`) whatever the size. |
| If-conversion | `OptIfConversionDsc::optIfConvert` (`ifconversion.cpp`): never inside a loop (block weight above 1.05, or the join reaches the start), and only when each arm costs at most 7. | PF-15 emits explicit branch-free forms for integer selects. |
| Integer min and max | `impIntrinsic` (`importercalls.cpp`): integral `Math.Min`/`Max` are intrinsics only on RISC-V with Zbb; elsewhere they are ordinary methods. | PF-18's suggested `Math.Max` would be a branch in loops. |
| Forward substitution | `fgForwardSubStatement` (`forwardsub.cpp`): a stored value is substituted into the next statement when that is its last use (liveness), so shared locals qualify. | Chains like GEP, load and use fold into address modes and memory operands (seen in the kernels' disassembly). |
| `unaligned.` | Codegen and lowering for ARM64 (`codegenarmarch.cpp`, `lowerarmarch.cpp`): no acquire/release forms for unaligned volatile accesses, no folding of an unaligned load into a following extension. No effect on x64. | Every load and store has `unaligned. 1`. |
| Settings | `JitHost::getIntConfigValue` (`vm/jithost.cpp`): JIT settings come from `DOTNET_*` environment variables only. `EEConfig` (`vm/eeconfig.cpp`) reads `System.Runtime.TieredCompilation.QuickJit`, `QuickJitForLoops`, `CallCountThreshold` and `CallCountingDelayMs` from `runtimeconfig.json` (present in 10.0.14 and 11 RC2). | The emitted `runtimeconfig.json` sets only `TieredPGO` (PF-06). |

## The JIT's limits in translated programs

`ilstats` computes RyuJIT's MinOpts inputs from an image's metadata: IL bytes, IL instructions without prefixes, blocks (branch targets, fall-throughs and handler boundaries), locals and local references. It also counts what shorter encodings and values kept on the evaluation stack would remove. On ffmpeg's decode harness (`decode.bc` from `artifacts/ffmpeg-browser-portable`, translated by `ee9304b`), it counts:

- 7,535 methods with bodies; 49 over a MinOpts limit, 48 of them on the instruction count and one on the locals alone;
- 34 over 60,000 IL bytes and 38 over 2,000 locals;
- 150 with at least 921 locals, so nothing is inlined into them, and 127 with untracked locals.

The H.264 decode under the JIT (`DOTNET_JitDisasmSummary=1`) runs seven of them as MinOpts code. Every one exceeds the instruction and local limits, and `ff_er_frame_end` is under the IL-size limit:

| Method | IL bytes | IL instructions | Locals | Local references | Limits exceeded |
| --- | ---: | ---: | ---: | ---: | --- |
| `ff_h264_decode_mb_cabac` | 169,065 | 74,166 | 6,505 | 17,394 | IL, instructions, locals, references |
| `ff_h264_filter_mb_fast` | 151,223 | 62,206 | 5,242 | 14,358 | IL, instructions, locals, references |
| `ff_h264_filter_mb` | 79,696 | 33,752 | 3,126 | 8,111 | IL, instructions, locals, references |
| `ff_h264_queue_decode_slice` | 71,807 | 29,469 | 2,444 | 7,239 | IL, instructions, locals |
| `ff_h264_pred_direct_motion` | 61,780 | 25,459 | 2,046 | 6,306 | IL, instructions, locals |
| `hl_decode_mb_complex` | 61,774 | 26,303 | 2,431 | 6,864 | IL, instructions, locals |
| `ff_er_frame_end` | 57,616 | 25,201 | 2,520 | 6,482 | instructions, locals |

Each is compiled twice: `Tier-0 switched to FullOpts, then to MinOpts` at the first call (they have loops and cannot use on-stack replacement), then `Tier-0 switched MinOpts` for the Tier1 request, which is discarded. Marking the seven `AggressiveOptimization` (a Mono.Cecil rewrite of the image) leaves one compilation each, with the same hashes.

BE-33's local sharing, in the working tree's compiler at the time of this review (built 01:27), was used to translate the same bitcode. It changes the picture: no method keeps more than 2,000 locals (`ff_h264_decode_mb_cabac` has 97), and none has 921 or more, so inlining works everywhere. But 26 methods are still over a limit: 25 on local references (sharing leaves every `stloc` and `ldloc` in place), 21 on the instruction count and 17 on IL size. `--diagnose-limits`, which models IL size, blocks and locals but not the other two triggers (BE-46), reports 16. `ff_er_frame_end` comes under every limit; `ff_h264_filter_mb` is just over on instructions (20,060) and references (8,111). The census is `results/ffmpeg-ilstats-be33.tsv`. The unshared build counts locals stored once and loaded once in their block. Keeping those values on the evaluation stack would save two references and two instructions each and bring 9 of the 26 under every limit. That is an upper bound: it assumes every such value can move to its use.

In the unshared build, too, shorter encodings and stack-kept values do not bring the largest methods under 20,000 instructions. In `ff_er_frame_end`, the 2,016 locals stored and loaded once within a block and the 1,045 `ldc.i8` would save at most about 5,000 instructions, leaving about 20,100. For these sizes, outlining (BE-26) or AOT compilation is the remedy.

## Experiments

The switches in `experiments.patch`, read from `LLVMNET_EXPERIMENT` (comma-separated):

- `switch`: an LLVM `switch` on at most 32 bits with at least 4 cases and a range of at most 4 times the case count becomes `ldc.i4 low; sub; switch (...)`, with one edge stub per target. For 32-bit conditions it uses whichever of the signed and unsigned ranges is smaller; others keep the chain.
- `initonly`: `__global*` and `__constant*` fields get `FieldAttributes.InitOnly`, except thread-local globals.
- `blk`: `llvm.memcpy` with a constant size of at most 256 and no volatile flag becomes `unaligned. 1 cpblk`, and `llvm.memset` with a constant size and a constant byte `initblk` (in a second worktree; a first version also took variable bytes).

For each A/B, one linked bitcode file was translated by the same compiler build with the switch off and on. Both images were timed with `scripts/ab-time.py` (alternating order, medians of 5 to 7 runs, in a memory-capped scope). Every output and every ffmpeg frame hash matched native.

### Switch dispatch

`interp.c` runs a 40-case `switch` over bytecode from a fixed pseudo-random generator, for 163,840,000 instructions in total. The program length sets how predictable the dispatch is:

| Program length | Native (jump table) | JIT, compare chain | JIT, CIL `switch` | NativeAOT, chain | NativeAOT, `switch` |
| --- | ---: | ---: | ---: | ---: | ---: |
| 16 ops | 0.127 s | 0.603 s | 0.203 s | | |
| 256 ops | 0.144 s | 0.580 s | 0.222 s | 0.474 s | 0.171 s |
| 4,096 ops (unpredictable) | 0.904 s | 0.560 s | 1.085 s | | |

With predictable dispatch, which is the interpreter's usual case, the CIL `switch` is 2.6 to 3 times faster than the chain and within 1.2 to 1.6 times native. With unpredictable dispatch the chain is faster than both jump tables on this CPU, native's included; the CIL `switch` behaves as native does in both regimes. RyuJIT compiles the switch to a bounds check and an indirect jump through a table of offsets.

### Global addresses as `static readonly`

`globals.c` runs `saxpy` and a histogram loop over global arrays, 60,000 rounds:

| Engine | Native | Plain fields | `initonly` fields |
| --- | ---: | ---: | ---: |
| JIT | 0.282 s | 0.584 s | 0.513 s |
| NativeAOT | | 0.472 s | 0.475 s |

In the JIT's `saxpy` loop, each array base is `mov rdi, qword ptr [rcx]` from the field before the change and an immediate (`mov rdx, 0x55BD885CDD40`) after it. RyuJIT rematerializes the constant at each use rather than keeping it in a register, but the load and its dependency are gone. Under NativeAOT the fields are not folded: ILC cannot run `Program`'s type initializer at compile time. The output is correct, so `initonly` is safe there, but it gives nothing; PF-17's hoisting is the NativeAOT fix. `stsfld` into the `initonly` fields from `Program`'s type initializer, which belongs to another class, is accepted by the JIT, NativeAOT and crossgen2.

### Small block copies

`blk.c` copies a 48-byte structure and clears 40 bytes of a buffer 41 million times. `blk0.c` is the same with a constant-zero clear:

| Kernel | Native | JIT | JIT, `blk` | NativeAOT | NativeAOT, `blk` |
| --- | ---: | ---: | ---: | ---: | ---: |
| `blk0.c` (constant byte) | 0.042 s | 0.098 s | 0.100 s | 0.049 s | 0.050 s |
| `blk.c` (variable byte), copies only | 0.053 s | 0.261 s | 0.264 s | 0.097 s | 0.087 s |
| `blk.c`, first version (variable `initblk` too) | 0.051 s | 0.262 s | 0.162 s | 0.096 s | 0.112 s |

The tiered JIT already inlines `Memory.Copy` through `NativeMemory.Copy` and `Buffer.Memmove` down to the moves `cpblk` gives (`vmovdqu ymm0, [r9]; vmovdqu xmm1, [r9+0x20]; ...`), and a constant-zero `Memory.Set` likewise. A variable byte reaches `SpanHelpers.Fill`, which is slower than the `rep stosb` RyuJIT emits for a variable-byte `initblk` under the JIT and faster under NativeAOT. NativeAOT gains 10% from `cpblk` for the copies. `cpblk` would also help methods the JIT does not inline into (921 locals or more), which this kernel does not test. The ffmpeg H.264 decode under the JIT moved from 2.836 s to 2.794 s with the first version, within noise.

### ffmpeg

The decode harness, one thread, 1080p clips (medians of 5):

| Image | H.264 | HEVC 10-bit |
| --- | ---: | ---: |
| native | 1.436 s | 2.365 s |
| JIT | 2.908 s | 3.344 s |
| JIT, `initonly,switch` | 2.862 s | 3.326 s |
| JIT, ReadyToRun | 2.113 s | 2.842 s |
| JIT, `initonly,switch`, ReadyToRun | 2.130 s | 2.803 s |

The CIL changes are within 1.6% here. ffmpeg's hot switches are few and its globals are reached through context structures.

### The benchmark subset

22 benchmarks of the PF-01 set: the linked bitcode of the `m8-final` run, translated by the scratch compiler, outputs checked against native (`exact`, or the set's numeric and status comparisons). Medians of 5 alternating runs in seconds; jpeg, lame, sha and tramp3d are dominated by start-up and compilation:

| Benchmark | Native | JIT | JIT, `initonly,switch` | JIT, ReadyToRun | JIT, both | JIT, .NET 11 RC2 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| dhrystone | 0.090 | 0.165 | 0.148 | 0.202 | 0.203 | 0.158 |
| richards | 0.361 | 0.494 | 0.483 | 0.499 | 0.491 | 0.488 |
| huffbench | 4.535 | 5.766 | 5.754 | 5.876 | 5.788 | 5.801 |
| hash | 1.254 | 2.095 | 2.106 | 1.993 | 2.030 | 2.073 |
| lists | 1.616 | 3.060 | 3.004 | 3.014 | 2.964 | 3.012 |
| heapsort | 1.662 | 1.575 | 1.621 | 1.688 | 1.713 | 1.527 |
| fannkuch | 1.408 | 1.488 | 1.474 | 1.585 | 1.565 | 1.489 |
| salsa20 | 2.003 | 2.256 | 2.278 | 2.173 | 2.152 | 2.257 |
| lpbench | 1.041 | 1.353 | 1.287 | 1.249 | 1.205 | 1.367 |
| linpack | 0.904 | 1.121 | 1.109 | 1.006 | 0.992 | 1.113 |
| whetstone | 0.451 | 0.681 | 0.680 | 0.557 | 0.554 | 0.677 |
| almabench | 2.384 | 2.455 | 2.555 | 2.376 | 2.445 | 2.512 |
| olden-bh | 0.403 | 1.181 | 1.178 | 1.146 | 1.100 | 1.201 |
| ptrdist-yacr2 | 0.165 | 0.389 | 0.386 | 0.356 | 0.348 | 0.392 |
| sha | 0.008 | 0.067 | 0.071 | 0.058 | 0.056 | 0.072 |
| jpeg | 0.003 | 0.084 | 0.078 | 0.040 | 0.040 | 0.078 |
| lame | 0.055 | 0.333 | 0.331 | 0.146 | 0.142 | 0.320 |
| printf | 0.902 | 1.959 | 1.956 | 1.951 | 1.935 | 1.979 |
| bullet | 1.364 | 2.567 | 2.697 | 2.125 | 2.130 | 2.626 |
| functionobjects | 1.822 | 2.753 | 2.774 | 2.353 | 2.390 | 2.805 |
| tramp3d | 0.075 | 0.646 | 0.616 | 0.183 | 0.183 | 0.692 |
| stepanov-abstraction | 1.465 | 1.627 | 1.665 | 1.561 | 1.518 | 1.653 |
| geometric mean against the JIT | | 1 | 0.994 | 0.847 | 0.840 | 1.002 |

The JIT here is 2.13 times native (geometric mean). The CIL changes range from 0.90 (dhrystone) to 1.06 times the JIT. bullet and almabench were rerun with 9 rounds and each switch alone:

| Benchmark | Base | Both | `initonly` | `switch` |
| --- | ---: | ---: | ---: | ---: |
| bullet | 2.569 | 2.580 | 2.581 | 2.615 |
| almabench | 2.433 | 2.510 | 2.467 | 2.486 |

bullet is unchanged within noise. almabench is 3% slower, from both switches: its time is in musl's `sin`, `cos` and `atan2`, each with a 4-case switch that became a jump table, and for four cases the chain was the faster form here. JP-03 tunes the minimum case count against it.

## The tiering delay

`TieredCompilationManager` (`vm/tieredcompilation.cpp`) does not count calls while its delay is active. Each method called for the first time in that window is queued and marks the window as "recently recorded", and the timer tick then re-arms for another `CallCountingDelayMs` (100 ms) instead of starting to count. Translated programs keep reaching functions they have not called yet, so their hot call-heavy code stays at Tier0. Loops are less affected, because on-stack replacement promotes them. PF-06 measured `TieredPGO`, `TieredCompilation` and `TC_QuickJitForLoops`, but not this delay. It can be set in `runtimeconfig.json` as `System.Runtime.TieredCompilation.CallCountingDelayMs`; the measurement used `DOTNET_TC_CallCountingDelayMs=0`, which has the same effect, on the IL images of the subset (medians of 5, or 3 for the second group):

| Benchmark | Default | Delay 0 | Ratio |
| --- | ---: | ---: | ---: |
| lame | 0.327 | 0.186 | 0.57 |
| tramp3d | 0.692 | 0.507 | 0.73 |
| bullet | 2.572 | 1.944 | 0.76 |
| ptrdist-yacr2 | 0.387 | 0.297 | 0.77 |
| whetstone | 0.684 | 0.543 | 0.79 |
| functionobjects | 2.772 | 2.229 | 0.80 |
| hash | 2.144 | 1.935 | 0.90 |
| stepanov-abstraction | 1.655 | 1.498 | 0.91 |
| olden-bh, linpack, almabench, salsa20, printf | | | 0.93 to 0.96 |
| lists, fannkuch, lpbench, huffbench, richards | | | 0.98 to 1.01 |
| dhrystone | 0.157 | 0.167 | 1.06 |
| sha | 0.064 | 0.069 | 1.08 |
| heapsort | 1.466 | 1.592 | 1.09 |
| jpeg | 0.084 | 0.094 | 1.12 |
| geometric mean (22) | | | 0.911 |
| ffmpeg H.264 1080p, one thread | 2.956 | 2.593 | 0.88 |

heapsort's default runs spread from 1.459 to 1.824 s; the other three losses are programs of under 0.2 s. On 4 cores (`taskset -c 0-3`, the CI runner's size), with ReadyToRun images beside them:

| Program | Default | Delay 0 | ReadyToRun | ReadyToRun, delay 0 |
| --- | ---: | ---: | ---: | ---: |
| bullet | 2.626 | 1.892 | 2.110 | 1.781 |
| tramp3d | 0.609 | 0.472 | 0.166 | 0.154 |
| lame | 0.327 | 0.189 | 0.142 | 0.120 |
| functionobjects | 2.776 | 2.235 | 2.386 | 2.213 |
| ffmpeg H.264 | 2.899 | 2.625 | 2.113 | 2.107 |

The gains hold with 4 cores, and the delay and ReadyToRun add up where both apply. Compiling ffmpeg's seven over-limit methods once (`AggressiveOptimization`, `artifacts/jit-patterns` Cecil rewrite) gave 2.847 s against the default's 2.956 s in the same run.

## ReadyToRun

`crossgen2 decode.dll -o r2r/decode.dll -r <Microsoft.NETCore.App 10.0.14>/*.dll -r LlvmNet.Runtime.dll --targetos:linux --targetarch:x64 -O` takes 2 s for ffmpeg's decoder on 24 threads and grows the image from 27.6 to 45.8 MB. Its `--verbose` log lists 11 methods not compiled: 6 with `UInt128` in their signatures ("Abi unstable type ... requires runtime JIT") and 5 that always throw. The map file has optimized code for the seven over-limit H.264 methods, for example 24,970 bytes for `ff_er_frame_end` against 51,128 bytes of MinOpts code from the JIT, and 108,460 against 147,766 for `ff_h264_decode_mb_cabac`. At run time the JIT summary shows Tier1 requests for those seven switching to MinOpts, which the runtime discards, so the precompiled code stays. 689 methods are jitted in all, against 1,326 for the IL image.

Where ReadyToRun loses: a method whose hot loop runs from its first call is never replaced by on-stack replacement once it has precompiled code, and precompiled code does not know which classes are initialized. Programs that live in such loops run slower: dhrystone 22%, fannkuch and heapsort 7% (subset table). Start-up-dominated programs gain most: tramp3d 3.5 times faster, lame 2.3, jpeg 2.1. Over the 22 benchmarks ReadyToRun is 0.847 of the JIT's time.

The image is specific to its target. The linux-x64 image of dhrystone fails with `System.BadImageFormatException` under .NET 10 on macOS ARM64 and on Windows x64; the IL-only image built from the same bitcode runs on both. A portable image therefore cannot carry precompiled code (ADR-17).

## .NET 11

`dotnet exec --fx-version 11.0.0-rc.2.26504.105` ran the subset's IL images on the 11.0 RC2 runtime. The geometric mean against 10.0.14 is 1.002 and the ratios range from 0.93 (jpeg) to 1.07 (sha, tramp3d), within this host's noise for most. The limits, the delay and the forms above are the same in `main`. NativeAOT with ILCompiler 11 was not measured: ILCompiler 11.0 RC1 is in the NuGet cache, but the driver's NativeAOT project targets `net10.0` (JP-07).

## Reproducing

Apply `artifacts/jit-patterns/experiments.patch` to a worktree of `ee9304b`, link the sysroots, and build `src/LlvmNet.Compiler`. Then:

```bash
LLVMNET_EXPERIMENT=initonly,switch <worktree>/bin/llvmnet --runtime=portable program.bc -o exp/program.dll
~/.nuget/packages/microsoft.netcore.app.crossgen2.linux-x64/10.0.14/tools/crossgen2 base/program.dll -o r2r/program.dll -r '/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/*.dll' -r base/LlvmNet.Runtime.dll --targetos:linux --targetarch:x64 -O
LLVMNET_COMPILER=<worktree>/bin/llvmnet python3 artifacts/jit-patterns/subset.py 5 dhrystone,bullet
dotnet run --project artifacts/jit-patterns/ilstats -c Release -- program.dll 20000
DOTNET_JitDisasmSummary=1 DOTNET_JitStdOutFile=summary.txt dotnet program.dll
```

`delay.sh` and `delay2.sh` are the tiering runs; `results/` holds the logs, the JIT summaries of the H.264 decode with and without ReadyToRun, and `ilstats` for ffmpeg. Timings were taken in `systemd-run --user --scope -p MemoryMax=24G`; another session's NativeAOT build ran during part of the block-copy and delay measurements, so those were alternated and repeated rather than taken once.
