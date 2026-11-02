# Evidence: native extensions of the portable ABI, feasibility spike (2026-11-03)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 with Radeon 8060S (radeonsi, Mesa 26.1.6), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang 22.1.8, commit `95cf6bb`. Other hosts: Windows x64 (`fluorite`: Intel Arc A380, .NET runtime 10.0.10) and macOS ARM64 (`mainmac`: Apple M6, .NET runtime 10.0.14). Links and runs on the build host ran in memory-capped user scopes. Timings are single runs or the two runs shown; the host was lightly loaded.

The question, raised by the owner after the [library survey](2026-11-02-library-survey.md): can a portable (`dotnet64-v2`) program reach a small set of standardized native libraries, such as EGL, OpenGL ES and Vulkan, without making the whole program depend on a host ABI, so that the same image still runs on Linux, Windows and macOS and loads the host's implementation where one exists? The survey had stopped at this point: ANGLE's GL backend in portable mode failed with "Could not dlopen native EGL: Dynamic loading not supported". The design the spike supports is [plan/11-native-extensions.md](../plan/11-native-extensions.md) and [ADR-15](../plan/decisions.md#adr-15-native-extensions-of-the-portable-abi).

## What was built

Everything is in the build host's `artifacts/native-ext/` (gitignored; `README.md` and `run.sh` there), using only what the toolchain already has: no compiler or runtime change.

- **A generator** (`gen.py`, 170 lines of Python) reads the Khronos registry files `gl.xml` and `egl.xml` (from the ANGLE checkout's `third_party/OpenGL-Registry` and `EGL-Registry`) and writes one C# stub per command, 3,458 in all (3,301 GL, 157 EGL; 27,669 lines). A stub is a `[CExport("name")]` method with the CIL signature llvmnet gives the C prototype (`int` for integers up to 32 bits, `long` for 64-bit, `nint` for pointers, `float`, `double`), which calls the native entry point through a `delegate* unmanaged` whose parameter types are the exact C types (`byte` for `GLboolean`, `uint` for `GLenum`, `nint` for `GLintptr`), bound by name on first use. A table maps every name to the stub's managed function pointer. Three commands whose callback types the spike does not thunk (`GLDEBUGPROCAMD`, the Android blob functions) throw when called.
- **A loader** (`Loader.cs`, 150 lines): per-OS names of the proxied libraries (`libEGL.so.1`/`libGLESv2.so.2`, `libEGL.dll`/`libGLESv2.dll`, `libEGL.dylib`/`libGLESv2.dylib`, with `LLVMNET_EXT_GLES_DIR` naming a directory), loaded with `NativeLibrary.TryLoad` at run time; binding through the host's `eglGetProcAddress` with `dlsym` as fallback; the proxy's own `eglGetProcAddress`, which returns the stub's managed pointer exactly when the host provides the function; `dlopen`, `dlsym`, `dlclose` and `dlerror` for the proxied names; and a reverse thunk for the GL debug callback (`[UnmanagedCallersOnly]`, calling the portable function through its managed pointer).
- **The assembly** `LlvmNet.Ext.Gles.dll` (817 KB), built with the .NET SDK against `LlvmNet.Runtime.dll`, carries `llvmnet.abi = dotnet64-v2` and `llvmnet.version = 0.3.0` as assembly metadata, so `--cil-reference` accepts it as a managed library of the portable ABI ([linking.md](../user/linking.md#managed-libraries)). It is one platform-neutral file: only the native libraries it loads differ by host.
- **A shim object** (`dl-shim.c`) defines `dlopen`, `dlsym`, `dlclose` and `dlerror` over the proxy's exports. A strong definition in an object keeps musl's static stubs (weak `dlopen` and `__dlsym`, strong `dlsym`, `dlclose`, `dlerror`) from being extracted from `libc.a`, so no sysroot change was needed. In the design these calls go through host entry points of `LlvmNet.Runtime`.
- **Two programs.** `direct.c` is a plain GLES 2 program (EGL through Mesa's surfaceless platform or the default display, a 64x64 pbuffer, a shader triangle, two pixels read back; `debug` also registers a `GL_KHR_debug` callback obtained through `eglGetProcAddress` and provokes `GL_INVALID_ENUM`; `bench N` times N `glGetError` calls directly and through the pointer `eglGetProcAddress` returns). The second is the survey's `angle-gl-smoke.c`, unchanged, linked against the survey's portable `libGLESv2_static.a` (ANGLE chromium/7922 compiled to bitcode), so ANGLE's unmodified GL backend loads `libEGL.so.1` through `dlopen`, fetches every EGL and GL function through `eglGetProcAddress`, and draws through the host's driver.

```sh
bin/llvmnet-clang -O2 -DUSE_OZONE=1 -I<angle>/include -c direct.c -o direct.o
bin/llvmnet-clang direct.o --cil-reference LlvmNet.Ext.Gles.dll -o direct.dll
bin/llvmnet-clang++ smoke.o libGLESv2_static.a dl-shim.o --cil-reference LlvmNet.Ext.Gles.dll -o smoke-angle.dll
```

## Results

The same output as the native build in every configuration that was run. The reference pixel values are `255,128,64,255` inside the triangle and `0,0,255,255` outside.

| Program and host | Build | Pixels | `GL_KHR_debug` callback into portable code | Exit |
| --- | --- | --- | --- | --- |
| `direct`, Linux x64, Mesa radeonsi | native clang-22 | identical | 1 call, "GL_INVALID_ENUM in glEnable(0x12345)" | 0 |
| `direct`, Linux x64 | portable JIT | identical | 1 call, same message | 0 |
| `direct`, Linux x64 | portable NativeAOT (4.1 MB) | identical | 1 call, same message | 0 |
| `direct`, macOS ARM64, Electron's ANGLE over Metal (`vendor Google Inc. (Apple)`, "Apple M6, OpenGL 4.1 Metal - 91.7") | the same portable image, JIT | identical | 1 call, "glEnable: Enum 0x12345 is currently not supported." | 0 |
| `direct`, Windows x64, Electron's ANGLE over Direct3D 11 (Intel Arc A380) | the same portable image, JIT | identical | 1 call, same ANGLE message | 0 |
| ANGLE smoke `gles` and `gl` (EGL device over the host's Mesa), Linux x64 | portable JIT (14.2 MB image) | identical, renderer "ANGLE (AMD, AMD Radeon 8060S Graphics (radeonsi ...), OpenGL ES 3.2 Mesa 26.1.6-1)" | not exercised | 0 |
| ANGLE smoke `gles` and `gl`, Linux x64 | portable NativeAOT (12.8 MB) | identical | not exercised | 0 |
| ANGLE smoke `null` (no native library) | portable JIT | as native (null renderer) | | 0 |

On Windows and macOS the native EGL and GLES libraries were the ANGLE builds that Visual Studio Code's Electron ships (`C:\Program Files\Microsoft VS Code\<hash>\libEGL.dll`, `Visual Studio Code.app/Contents/Frameworks/Electron Framework.framework/Versions/A/Libraries/libEGL.dylib`), named through `LLVMNET_EXT_GLES_DIR`; Firefox's ANGLE copies would have served as well. The bundle copied to each host was four files: `direct.dll`, its `runtimeconfig.json`, `LlvmNet.Runtime.dll` and `LlvmNet.Ext.Gles.dll`.

Build cost on Linux (one run each): linking the ANGLE smoke program took 5.8 s and 557 MB under the JIT and 13.0 s and 1.26 GB to a NativeAOT executable (after TC-20; the survey measured 22.9 s for the system-mode link before it); `direct.dll` links in about a second and publishes as NativeAOT in 3.5 s.

**Per-call cost**, `glGetError` in a loop of 2,000,000 calls, nanoseconds per call, two runs each; "pointer" is the call through the function pointer `eglGetProcAddress` returned:

| Host and build | Direct call | Through the pointer |
| --- | --- | --- |
| Linux, native clang-22 (Mesa) | 7.3, 7.6 | 6.1, 7.2 |
| Linux, portable JIT | 8.6, 8.8 | 8.8, 8.9 |
| Linux, portable NativeAOT | 7.4, 8.1 | 7.8, 8.4 |
| macOS ARM64, portable JIT (ANGLE Metal) | 3.8 | 3.8 |
| Windows x64, portable JIT (ANGLE D3D11) | 5.2 | 5.4 |

The managed-to-native transition adds about 1 ns to a 7 ns native call on Linux (the JIT and NativeAOT inline the transition frame); the macOS and Windows figures have no native baseline on those hosts and show only that the whole call, transition included, costs a few nanoseconds there too. The first call of each entry point also pays the lookup through `eglGetProcAddress`, which was not measured.

## What the spike shows

- A portable program can call a host library through a managed proxy with **no native code generated at build time** and no compiler or runtime change: the existing `--cil-reference` mechanism, a `CExport` method per entry point, and .NET's own `NativeLibrary` and unmanaged function pointers are enough. The CLR applies the host's calling convention at run time, so one proxy assembly serves Linux x64, macOS ARM64 and Windows x64.
- **Function pointers cross the boundary both ways.** `dlsym` and `eglGetProcAddress` hand portable code managed pointers of the stubs, which `calli` calls like any translated function; ANGLE's loader, which fetches all of EGL and GL this way, worked unchanged. A portable callback given to native code is wrapped in an `[UnmanagedCallersOnly]` thunk, and Mesa's and ANGLE's debug output reached it on all three hosts.
- **Memory needs no marshalling.** Portable pointers are addresses in process memory, so vertex arrays, strings and `glReadPixels` buffers were read and written by the driver in place.
- **The per-call cost is about a nanosecond** on top of the native call.

## What it does not show

- The `dlopen` family is replaced by a shim object linked into the program, not by the runtime; a program that does not link the shim still gets musl's "Dynamic loading not supported". The design moves this into the musl port and `LlvmNet.Runtime` (NX-02).
- The proxy was hand-assembled from a registry: there is no manifest, no sysroot package, no driver option, no availability query, and no generated neutral headers (ANGLE's headers with `USE_OZONE` stood in for them). A missing host library surfaces as an `EntryPointNotFoundException` on the first call or a `dlopen` failure with the proxy's message.
- No structure is passed by value and no variadic function is called: EGL and GL have none, so the typed-struct path of the design (NX-07) is untested.
- The debug callback thunk has one slot (the last registered callback) and was only called on the registering thread; callbacks from threads a native library creates, which have no musl thread state, are not handled (NX-08).
- Windows and macOS ran the JIT only; NativeAOT executables for those hosts were not published. The ANGLE smoke program ran on Linux only, because the survey's portable ANGLE archive assumes `__linux__` for its own sources (ADR-14), which does not affect the proxy.
- The Windows and macOS runs used third-party ANGLE binaries found on the hosts; nothing checks their version, and a test on CI would need a pinned ANGLE build or Mesa's `llvmpipe` where a GPU is absent.
- The generator covers the Khronos XML form only; C-header input (SDL, GLFW) and Vulkan's registry, with function pointers inside structures, are design items (NX-09, NX-10).
