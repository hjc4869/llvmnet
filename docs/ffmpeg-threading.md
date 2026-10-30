# Threaded ffmpeg

`scripts/build-ffmpeg.sh` builds ffmpeg's libraries with pthreads and a decode harness for one runtime mode. Inline and x86 assembly, runtime CPU detection, networking, hardware acceleration, filters and devices are disabled. `scripts/build-ffmpeg-core.sh` builds a smaller single-threaded profile into `artifacts/ffmpeg-core`.

## Build

```sh
LLVMNET_RUNTIME=system bash scripts/build-dependencies.sh
LLVMNET_RUNTIME=system bash scripts/build-ffmpeg.sh
LLVMNET_RUNTIME=portable bash scripts/build-dependencies.sh
LLVMNET_RUNTIME=portable bash scripts/build-ffmpeg.sh
```

`LLVMNET_RUNTIME` defaults to `portable`. Outputs go to `artifacts/deps-<mode>`, `artifacts/deps-build-<mode>` and `artifacts/ffmpeg-browser-<mode>`; `LLVMNET_DEPS_PREFIX`, `LLVMNET_DEPS_BUILD_DIR` and `FFMPEG_BUILD_DIR` override them. Each directory records its runtime mode, and the scripts refuse to reuse a directory built for the other mode. Portable build directories also record the sysroot they were built against and start over when it changes.

The sources are the pinned ffmpeg, dav1d, libxml2 and zlib releases from `scripts/sources.lock`, fetched by `scripts/fetch-sources.sh`; `FFMPEG_SOURCE`, `DAV1D_SOURCE`, `LIBXML2_SOURCE` and `ZLIB_SOURCE` select other source trees. The builds need .NET 10, Clang/LLVM 22, CMake, Ninja, Meson and pkg-config. The CMake and Meson cross files ([cmake/llvmnet.cmake](../cmake/llvmnet.cmake), [meson/llvmnet.ini](../meson/llvmnet.ini)) run llvmnet's output executables directly for configure probes.

## Decode

```sh
dotnet artifacts/ffmpeg-browser-system/decode.dll input.mp4 100 video 4 frame
dotnet artifacts/ffmpeg-browser-portable/decode.dll input.mp4 100 video 4 slice
```

The arguments are `input [frame-limit] [video|audio] [threads:1-64] [auto|frame|slice] [hash|bench]`. The defaults are ten frames of video, one thread and `hash`, which prints a hash of every frame. `bench` counts and drains frames without hashing or per-frame output. With an explicit thread count the harness writes the decoder name, the configured count and ffmpeg's active thread type to stderr; frame hashes stay on stdout. A codec that does not support the requested threading mode decodes without ffmpeg worker threads, which the tests detect through the active thread type. dav1d manages its own workers and reports thread type zero. The [SIMD128 profile](simd128.md) uses the same harness.

The scalar system build also links `decode-native` from the same bitcode with Clang. It is the reference decoder for the tests of both modes.

System-mode threads are native pthreads with managed adapters for callbacks and thread-local storage. Portable-mode threads are musl's pthreads running on managed threads. libxml2 is built without thread support.

## Tests

```sh
bash tests/ffmpeg-threads.sh
FFMPEG_THREADS_AOT=1 bash tests/ffmpeg-threads.sh
FFMPEG_THREADS_SKIP_BUILD=1 FFMPEG_THREADS_AOT=1 bash tests/ffmpeg-threads.sh
```

The test builds both modes and compares threaded decodes under the JIT with single-thread hashes from the native decoder. `FFMPEG_THREADS_AOT=1` also publishes and runs both NativeAOT decoders, which needs the .NET NativeAOT tools. `FFMPEG_THREADS_SKIP_BUILD=1` reuses existing builds. `FFMPEG_THREADS_TIMEOUT` sets the per-process timeout (default 120 seconds). Generating the fixtures needs a host ffmpeg with the libx264, libx265, libvpx-vp9 and libaom-av1 encoders. Logs, fixtures, hashes and thread diagnostics go to `artifacts/tests/ffmpeg-threads/`.

| Decoder | Thread modes | Thread counts | Drain and teardown |
| --- | --- | --- | --- |
| H.264 | frame, slice | 2, 4 | 24-frame drain at end of file, close after 3 frames |
| 10-bit HEVC | frame, slice | 2, 4 | same |
| VP9 | frame | 2, 4 | same |
| dav1d AV1 | its own workers | 2, 4 | same |

The test also requires the portable decoder to have no P/Invoke imports. Variadic callbacks from system libxml2 are covered by [tests/varargs-callbacks.sh](../tests/varargs-callbacks.sh).

## Limits

The tests cover the decoders above, not ffmpeg's FATE suite. Concurrent use of several demuxer or decoder contexts, every enabled decoder, long-running cancellation and error paths, and operating systems and architectures other than Linux x86-64 are not tested. libxml2 must not be used from several threads at once.
