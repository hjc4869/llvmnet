# Threaded ffmpeg

The full browser-profile builder enables pthreads while keeping assembly, runtime CPU dispatch, networking, hardware acceleration, filters, and devices disabled. Upstream ffmpeg and dependency sources are not modified. The smaller core bring-up profile is unchanged.

## Build

```sh
LLVMNET_RUNTIME=system bash scripts/build-dependencies.sh
LLVMNET_RUNTIME=system bash scripts/build-ffmpeg.sh
LLVMNET_RUNTIME=portable bash scripts/build-dependencies.sh
LLVMNET_RUNTIME=portable bash scripts/build-ffmpeg.sh
```

System outputs default to `artifacts/deps-system`, `artifacts/deps-build-system`, and `artifacts/ffmpeg-browser-system`; portable outputs use the corresponding `-portable` suffix. Omitting `LLVMNET_RUNTIME` retains the legacy managed-host directories. Directory markers and bitcode ABI tags prevent mixing already-tagged outputs. Use separate `LLVMNET_DEPS_PREFIX`, `LLVMNET_DEPS_BUILD_DIR`, and `FFMPEG_BUILD_DIR` overrides for independent builds.

Source/tool prerequisites remain those of the existing build: .NET 10, Clang/LLVM 22, CMake/Ninja, Meson, pkg-config, the LightPlayer-Packages sources, and zlib 1.3.2. `LIGHTPLAYER_PACKAGES`, `FFMPEG_SOURCE`, and `ZLIB_SOURCE` override source locations. CMake and Meson execute llvmnet's generated launchers directly for executable probes.

## Decode

```sh
dotnet artifacts/ffmpeg-browser-system/decode.dll input.mp4 100 video 4 frame
dotnet artifacts/ffmpeg-browser-portable/decode.dll input.mp4 100 video 4 slice
```

Arguments are `input [frame-limit] [video|audio] [threads:1-64] [auto|frame|slice] [hash|bench]`. Defaults preserve the previous ten-frame, one-thread video behavior. `bench` counts/drains frames without hashing or per-frame output for decode-only measurements. An explicit thread count writes the decoder name, configured count, and active ffmpeg thread type to stderr; frame hashes remain on stdout. Unsupported codec threading modes may fall back to no ffmpeg worker mode, so the regression asserts activation for codecs expected to provide it. dav1d manages its own workers and reports active ffmpeg thread type zero. The optional [SIMD profile](simd128.md) keeps this same threading interface.

The system build also creates `decode-native` from the linked system bitcode for reference comparisons. Portable bitcode is not linked against host libc to manufacture a native baseline, since that would mix ABIs.

## Verification

```sh
bash tests/ffmpeg-threads.sh
FFMPEG_THREADS_AOT=1 bash tests/ffmpeg-threads.sh
FFMPEG_THREADS_SKIP_BUILD=1 FFMPEG_THREADS_AOT=1 bash tests/ffmpeg-threads.sh
```

The default test builds both ABI profiles and runs JIT comparisons. `FFMPEG_THREADS_AOT=1` additionally publishes and runs both NativeAOT decoders, requiring the .NET NativeAOT SDK/link tools. `FFMPEG_THREADS_SKIP_BUILD=1` reuses explicitly prepared builds, not dependency/configuration checks. Set `FFMPEG_THREADS_TIMEOUT` to change the per-process 120-second timeout. Fixture generation requires host ffmpeg with libx264, libx265, libvpx-vp9, and libaom-av1 encoders. Build logs, fixtures, hashes, and thread diagnostics stay in `artifacts/tests/ffmpeg-threads/`.

Verified on Linux x86-64 with LLVM 22.1.8 and .NET 10.0.11:

| Decoder | Thread Modes | Counts | Drain/Teardown |
| --- | --- | --- | --- |
| H.264 | frame, slice | 2, 4 | 24-frame EOF drain and 3-frame early close |
| 10-bit HEVC | frame, slice | 2, 4 | same |
| VP9 | frame | 2, 4 | same |
| dav1d AV1 | internal workers | 2, 4 | same |

All 96 managed combinations matched single-thread native frame hashes, as did 24 threaded native comparisons. The portable decoder inspection reports 7,202 IL method bodies and zero generated P/Invoke imports. Four-thread ten-frame checks on the supplied 3840x2160 HLG HEVC video also matched native in both ABIs and execution modes. The 31 existing codec fixtures, DASH/HLS, and scaling/resampling/library tests passed on the threaded legacy profile.

System threads are real pthreads using native synchronization and managed callback/TLS adapters. Portable threads use the managed pthread implementation. No native codec implementations are substituted in either mode. Internal variadic callback handling needed by system libxml2 is covered separately by [../tests/varargs-callbacks.sh](../tests/varargs-callbacks.sh).

## Limits

This is focused decoder coverage, not the complete upstream FATE suite or a performance measurement. It does not validate arbitrary concurrent demuxer/decoder contexts, every enabled legacy decoder, long-running cancellation/error paths, or execution on other operating systems/architectures. libxml2 still uses its single-threaded configuration; concurrent XML access from unrelated callers is not claimed. Portable public declarations added during dependency builds do not imply complete libc/POSIX or scaler runtime coverage. The original full-file HDR timings were single-threaded and are not threading benchmarks.