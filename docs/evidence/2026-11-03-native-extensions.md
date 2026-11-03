# Evidence: native extensions of the portable ABI, implementation (2026-11-03)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 with Radeon 8060S (radeonsi, Mesa 26.1.6), 124 GB RAM, .NET SDK 10.0.403, LLVM/Clang 22.1.8, Python 3.14. Branch `claude/fervent-bhabha-ff2fe0` (NX-02 and NX-03 at `3f785ca` and `f86eddd`, then the commits named below). Test runs on the build host ran in memory-capped user scopes. The design is [plan/11-native-extensions.md](../plan/11-native-extensions.md) and [ADR-15](../plan/decisions.md#adr-15-native-extensions-of-the-portable-abi), accepted by the owner on 2026-11-03; the feasibility spike before it is [2026-11-03-native-extension-spike.md](2026-11-03-native-extension-spike.md).

## NX-04: the generator

`scripts/ext-gen.py` (926 lines of Python) turns an extension description and its Khronos registries into the stubs, the table, the reverse thunks and the registration class of `LlvmNet.Ext.<Name>.dll`, the package's headers and manifest, and the check files; `src/LlvmNet.Runtime/HostLibraries.cs` is the runtime support the generated code calls (host library search, binding, thunk slots, the exit on a missing function); `scripts/build-extensions.sh` builds, checks and installs. [dev/extensions.md](../dev/extensions.md) describes all three.

```sh
scripts/build-extensions.sh gles
tests/run.sh ext-gen extensions driver abi-freeze host
dotnet test tests/LlvmNet.UnitTests -c Release --filter 'FullyQualifiedName~HostLibrariesTests|FullyQualifiedName~NativeExtensionsTests'
```

| What | Result |
| --- | --- |
| `gles` from `egl.xml` and `gl.xml` (pinned: EGL-Registry `e964211`, OpenGL-Registry `6af574a`), with no hand edit | 3,349 entry points proxied (EGL 1.5 and extensions; GLES 2.0 to 3.2, desktop GL and their extensions); 23 left out because they use `GLhandleARB`, whose size differs on macOS; `glGetVkProcAddrNV` fails when called, because it returns a host function pointer; reverse thunks for 7 callback types |
| Output | `Generated.cs` 33,797 lines; `LlvmNet.Ext.Gles.dll` 865,280 bytes; 12 headers (Khronos's, with a neutral `EGL/eglplatform.h` whose native handles are `void *`) |
| Checks | the two check files (EGL with GLES 3.2 and its extensions; desktop GL core with its extensions) compile with the headers and the registry's own declarations together; all 3,350 stubs match the declarations llvmnet compiles, narrow integers' `signext`/`zeroext` included; the link of the check objects against the assembly resolves every stub |
| Cost | 4.8 s and 205 MB for generation, the assembly build, the checks and the installation (`build-extensions.sh --force gles`) |
| Reproducibility | generating twice gives identical files; two forced builds give the same assembly (SHA-256 `c7f7015d...`) |
| Mutation check | changing the expected signedness of `glIsEnabled`'s result and `glColor3b`'s first parameter in `signatures.json` makes `ext-gen.py verify` fail naming both; declaring `glClear` as `int glClear(int)` makes the check link fail with "Runtime ABI mismatch for glClear: i32 (i32)" |

`tests/ext-gen.sh` (new, tags `core aot`) builds `tests/extensions/generated/registry.xml`, a synthetic registry in the Khronos format, into an extension: 24 entry points in two host libraries (one preloaded by the other, whose `DT_NEEDED` it satisfies when the first is loaded by path), narrow signed and unsigned integers, 64-bit and pointer-sized integers, `float` and `double`, strings, a buffer written in place, an opaque handle, two callback types with pools of two thunks, a callback type without thunks, a lookup function, a default implementation, an excluded platform type and an entry point that returns a host function pointer. Its host libraries are compiled natively with clang, and `tests/extensions/generated.c` is built three ways:

| Build | Result |
| --- | --- |
| native clang-22 | the reference output, 31 lines |
| portable, `-lllvmnet-gen`, JIT | identical |
| portable, NativeAOT | identical |
| system mode, `--extension=generated` | identical; `llvmnet_ext_available("generated")` is 1 and `llvmnet_ext_library` names the libraries the system loader linked |
| portable, host libraries present | available; a direct call of an entry point the host only provides through its lookup function binds through it; `dlopen` and `dlsym` by the Linux names return callable stubs, `dlsym` through the main library finds the second library's function as on Linux |
| portable, host libraries absent | `llvmnet_ext_available` 0, `llvmnet_ext_library` names the candidates tried and the loader's message, the default implementation answers, the lookup function returns null, `dlopen` fails with the same reason, and the first call of an entry point without a default ends the program with one `llvmnet:` line and status 127 |
| portable, a third distinct callback for a pool of two | ends with "the native extension generated has entry points for 2 distinct GENlogproc callbacks, and the program passed another", status 127; a callback passed again reuses its thunk |
| portable, a callback type without thunks; the entry point that returns a host function pointer | a null callback passes; anything else, and the call of that entry point, ends with a message naming the entry point and the reason |
| rejected descriptions | a `long`, `unsigned long` or `long double` parameter, a structure by value and an unknown type each fail the generation with the entry point and the reason |

The run of the six neighboring tests (`ext-gen`, `extensions`, `driver`, `driver-reference`, `abi-freeze`, `host`) passed in 11 s; the 17 unit tests of the registry and the runtime support pass. The frozen header stamp is unchanged: `<llvmnet/ext.h>`, which declares `llvmnet_ext_available` and `llvmnet_ext_library`, is in `sysroot/extensions/include`, outside the `dotnet64-v2` header set, on the include path in both modes.

## NX-05: the gles extension

`extensions/gles/extension.json` describes EGL 1.5 (`egl.xml`, library `EGL`) and OpenGL ES 2.0 to 3.2 with desktop GL (`gl.xml`, APIs `gles2` and `gl`, library `GLESv2`), the host names of Mesa and ANGLE, `-lEGL` and `-lGLESv2`, `eglGetProcAddress` as the lookup function, reverse thunks for the seven callback types, defaults for the probing entry points and the required set of `llvmnet_ext_available`; [user/extensions.md](../user/extensions.md#native-extensions) lists them. The Khronos headers are installed unchanged except `EGL/eglplatform.h`, whose native handle types are `void *` (the registry's own `EGL_NO_PLATFORM_SPECIFIC_TYPES` choice) on every host.

```sh
tests/run.sh ext-gles                          # Linux x64: Mesa 26.1.6
tests/xp/build.sh                              # the bundle, with the gles fixture
LLVMNET_EXT_GLES_DIR=<ANGLE directory> bash run.sh --aot --jobs 3 .   # on each host
```

`tests/extensions/gles.c` initializes EGL headless (Mesa's surfaceless platform, else the default display), draws a triangle into a 64x64 pbuffer with GLES 2 and again with GLES 3 and a vertex array object, reads two pixels back, registers a `GL_KHR_debug` callback obtained through `eglGetProcAddress` and provokes `GL_INVALID_ENUM`, and loads EGL a second time as ANGLE's loader does (`dlopen("libEGL.so.1")`, `dlsym(eglGetProcAddress)`, every function through it). Its standard output, which every conforming host prints the same, is `tests/extensions/gles.expected`:

```
EGL initialized; GLES API bound: yes
GLES 2 pixels: inside 255,102,51,255 outside 0,0,255,255, glGetError 0x0
GL_KHR_debug: callback received, type GL_DEBUG_TYPE_ERROR, user pointer kept, glGetError 0x500
unknown entry point: null
GLES 3: vertex array bound, extension strings listed
GLES 3 pixels: inside 255,102,51,255 outside 0,0,255,255, glGetError 0x0
dlopen libEGL.so.1: eglGetProcAddress found, display initialized, glGetString found
```

| Host | Host libraries (renderer) | Builds that print the expected output |
| --- | --- | --- |
| Linux x64, this host | Mesa `libEGL.so.1` and `libGLESv2.so.2` (llvmpipe with `LIBGL_ALWAYS_SOFTWARE=1`; radeonsi, AMD Radeon 8060S, otherwise) | native clang-22 (llvmpipe), portable JIT (llvmpipe and radeonsi), portable NativeAOT (llvmpipe), system mode with `--extension=gles` (llvmpipe); the `tests/xp/` bundle's JIT image and NativeAOT executable |
| Windows x64, `fluorite` (.NET 10.0.301) | ANGLE from Visual Studio Code's Electron (`C:\Program Files\Microsoft VS Code\2c33b50b16`), "ANGLE (Intel, Intel(R) Arc(TM) A380 Graphics Direct3D11 vs_5_0 ps_5_0, D3D11-32.0.101.9033)", OpenGL ES 3.0 | the bundle's portable JIT image and the NativeAOT executable published on the host |
| macOS ARM64, `mainmac` (.NET 10.0.403) | ANGLE from Visual Studio Code's Electron framework, "ANGLE (Apple, Apple M6, OpenGL 4.1 Metal - 91.7)", OpenGL ES 3.0 | the bundle's portable JIT image and the NativeAOT executable published on the host |

The debug message differs by driver and goes to standard error: Mesa's "GL_INVALID_ENUM in glEnable(0x12345)", ANGLE's "glEnable: Enum 0x12345 is currently not supported.". The rest of the bundle passed on both other hosts as well (62 of 62 runs each: 52 fixtures under the JIT, 10 as NativeAOT). Natively, Mesa's libglvnd returns a dispatch stub from `eglGetProcAddress` for any unknown name that starts with `gl`, where the extension returns null; the program asks for an unknown `egl` name, for which both return null.

ANGLE's unmodified GL backend, the survey's portable `libGLESv2_static.a` (chromium/7922) with its smoke program, now links with `--extension=gles` and needs neither the spike's proxy nor its `dlopen` shim: musl's port serves `dlopen("libEGL.so.1")` from the extension (NX-02).

| ANGLE smoke on Linux x64 | Link | Result |
| --- | --- | --- |
| JIT image | 5.4 s, 560 MB | `gles` and `gl` backends on the host's Mesa: "ANGLE (AMD, AMD Radeon 8060S Graphics (radeonsi ...), OpenGL ES 3.2 Mesa 26.1.6-1)", OpenGL ES 3.0, the native pixels; the `null` backend as natively |
| NativeAOT executable (13.0 MB) | 11.8 s, 1.33 GB | `gles` and `gl` backends: the native pixels |

Not done in this item: the weekly job that rebuilds ANGLE and runs its smoke program. It depends on WG-07, whose pinned Skia and ANGLE builds are still the survey's local scripts, so it is recorded with WG-07.

## M10 exit criteria

| Criterion | Result |
| --- | --- |
| NX-02 to NX-06 done | `3f785ca`, `f86eddd`, `0483344`, `b354111`, `a425a6e` |
| A program using `gles` renders the native pixels on Linux x64, Windows x64 and macOS ARM64, JIT and NativeAOT | yes, on this host and through the `tests/xp/` bundle on `fluorite` and `mainmac` (above); in CI it needs the GitHub repository (FD-19), and the Windows and macOS runners need an ANGLE copy named by `LLVMNET_EXT_GLES_DIR` |
| Programs without extensions are byte-identical to before | the 51 portable fixtures of `tests/xp/fixtures.tsv` (C, C++ and Fortran) compiled by master's compiler (`941987e`) and by this branch's, both against this branch's sysroot with `gles` installed, are identical byte for byte once both compilers carry the same version stamp (`llvmnet.version` names the commit, so images from any two commits differ in it and in the image identity derived from the contents); the sysroot archives of both builds, 1,518 members, are identical after normalizing the build directory, except the five members NX-02 replaced (`dlopen`, `__dlsym`, `dlclose`, `dlerror`, `dladdr`) |
| The ABI freeze test passes unchanged | yes; the header stamp is unchanged, because `<llvmnet/ext.h>` and the extensions' headers are outside the frozen set |
| Core and toolchain gate | 103 of 103 tests pass (`tests/run.sh --tag core --tag toolchain --exclude-tag slow`, 291 s), with the two new tests `ext-gen` and `ext-gles` |

## NX-11: the cost of a call

`tests/extensions/gles.c bench <n>` times `glGetError` on Mesa (radeonsi), called directly and through the pointer `eglGetProcAddress` returns, and the first and second calls of 26 entry points the program has not called before. The runs below were pinned to one core (`taskset -c 4`; unpinned runs on this CPU vary by 2x) and interleaved, five rounds of 20 million calls each, minimum shown; JIT runs set `DOTNET_TC_CallCountingDelayMs=0`, because a 60 ms loop otherwise ends before the stub is recompiled at tier 1, and the default 100 ms delay is a start-up effect, not a steady-state one.

| Build | `glGetError`, direct | through the pointer |
| --- | --- | --- |
| native clang-22 | 1.8 ns | 1.4 ns |
| JIT, before NX-11 | 4.8 ns | 5.0 ns |
| JIT | 5.1 ns | 5.2 ns |
| JIT, `LLVMNET_EXT_GLES_NON_BLOCKING=1` | 2.2 ns | 2.4 ns |
| NativeAOT, before NX-11 | 3.8 ns | 3.8 ns |
| NativeAOT | 3.8 ns | 4.0 ns |
| NativeAOT, `LLVMNET_EXT_GLES_NON_BLOCKING=1` | 2.2 ns | 2.4 ns |

- **The GC transition is most of the overhead**: skipping it (`SuppressGCTransition`) leaves 0.4 to 0.8 ns over native. It is safe only for functions that never take a lock or call back, so the description marks them (`non-blocking`: `glGetError` and `eglGetError` in `gles`) and a run opts in: ANGLE's `glGetError` takes its share-context lock (`SCOPED_SHARE_CONTEXT_LOCK`), and a thread blocked on that lock without a transition can deadlock with a garbage collection that another thread, holding the lock in a debug callback, waits for.
- **Two compiler behaviors shaped the stub.** The first version tested the switch and made one of two calls through the same pointer, with and without the transition; NativeAOT's compiler merged the two calls into the one without the transition and dropped the test, so the default path skipped the transition too (seen in the disassembly: no `RhpPInvoke` and no load of the switch). The fast call now goes through a slot of its own, filled at binding only when the switch is on. Under the JIT the fast path then gained nothing, because the JIT initializes the transition frame in the prologue of any method that contains an ordinary unmanaged call; that call is now a method of its own, which costs 0.3 ns in the default mode for the marked functions only.
- **First calls.** Natively, the 26 first calls take 17 to 22 µs (lazy binding through libglvnd's dispatch), 0.4 µs the second time; from the NativeAOT executable 33 to 40 µs and 0.6 to 0.7 µs (binding costs about 0.6 µs more per function than the native loader's lazy binding); under the JIT 1.07 to 1.14 ms and 1.6 to 1.9 µs, which is the JIT compiling each stub. Binding every entry point at image start would cost about 4 ms under NativeAOT for `gles`'s 3,349 entry points, load the host libraries in programs that never draw, and change nothing about the JIT's compile cost, so binding stays lazy. A ReadyToRun build of the extension assembly would remove the JIT's share for one platform at a time; it is not done.

## NX-08: callbacks on threads the host library started

A reverse thunk now calls `NativeCallbacks.Enter` first; on a thread without a musl thread pointer it calls the musl port's `__llvmnet_attach_thread`, which builds a descriptor with the program's TLS and a TSD area as `pthread_create` does, sets the thread pointer and turns on locking; a finalizer calls `__llvmnet_detach_thread` once the thread has ended. The bitcode linker gained required symbols (`llvmnet_link`'s `required`, as a native linker's `-u`), so that the driver links the two functions into images that use an extension and no others.

`genFireOnThread` in the synthetic API calls the registered callback on a thread the native library starts with `pthread_create` and joins. The callback uses `errno` (`strtol` overflow), `malloc`, `snprintf` and `printf`, a `_Thread_local` counter, `pthread_self` against the main thread and a pthread key.

| Run of `generated threads` (3 threads, then 200 more) | Result |
| --- | --- |
| native | the reference: each thread sees `ERANGE`, its own thread, a fresh thread-local counter and an unset key, and the 200 more sum to 28,636 with no failed check |
| portable JIT, portable JIT with `DOTNET_GCgen0size=0x20000` (collections every few allocations), portable NativeAOT | identical |
| portable JIT with frequent collections, with a temporary trace in the finalizer (not committed) | 151 of the 203 attached threads were detached while the program ran; the rest were still pending at exit |
| before this item | the first callback faulted on the null thread pointer (`NullReferenceException` in the callback) |

Images that use no extension are unaffected: the attachment is linked only with an extension, and `libc.a` gains one member that nothing else extracts. `tests/ext-gen.sh`, `tests/ext-gles.sh`, `tests/extensions.sh` and `tests/abi-freeze.sh` pass.

## NX-10: the vulkan extension

`extensions/vulkan/extension.json` takes Vulkan 1.0 to 1.4 and its registered extensions from `vk.xml` (Vulkan-Headers 1.4.367, pinned), without the window-system and provisional extensions and `VK_LUNARG_direct_driver_loading`; the generator produces 775 entry points, all verified against the Khronos headers, with reverse thunks for the eight callback types and the layout assertions of the four structures it patches. The generator gained what `vk.xml` needs: command aliases, `api` filtering of commands, parameters, types and structures (it also holds Vulkan SC), comma-separated API lists, handle, enum and bitmask types, the newer function-pointer format, parameters with array suffixes, the integer values of enumerants (sType), structure layouts in the portable ABI, callbacks inside structures, hand-written hooks (`replace`, `after`, `sources`) and a lookup function of its own (`extensions/vulkan/Vulkan.cs`: `vkGetInstanceProcAddr` and `vkGetDeviceProcAddr`, binding through the last instance).

`tests/extensions/vulkan.c` creates an instance with application allocation callbacks and two debug messengers, one in the instance's `pNext` chain and one created through `vkGetInstanceProcAddr`, submits a message, creates a device, takes a queue through `vkGetDeviceProcAddr`'s pointer, fills a 64 KiB host-visible buffer with `vkCmdFillBuffer` on the device and reads it back, and checks that every allocation was freed. Its standard output is `tests/extensions/vulkan.expected`.

| Host and driver | Builds that print the expected output | Allocations through the program's callbacks | Messages to the chained messenger |
| --- | --- | --- | --- |
| Linux x64, Khronos loader 1.4.363, Mesa lavapipe | native, portable JIT, NativeAOT (3.4 MB), system mode | 316, all freed, 20 reallocations; the native build's counts | 66, as natively |
| Linux x64, Mesa RADV (AMD Radeon 8060S) | portable JIT | 551, all freed | 100 |
| Windows x64, `fluorite`: the system loader and Visual Studio Code's own `vulkan-1.dll`, Intel Arc A380 | the bundle's portable JIT image and NativeAOT executable, with each loader | 1,633, all freed | 54 to 66 (the loader's own diagnostics) |
| macOS ARM64, `mainmac`: SwiftShader (`libvk_swiftshader.dylib` from Visual Studio Code's Electron), no loader | the bundle's portable JIT image and NativeAOT executable | 10, all freed | none (no loader); the submitted message reaches the chained messenger instead of the created one, because SwiftShader attaches only the messenger it gets at instance creation (its `Instance::submitDebugUtilsMessage`), which the test accepts |

On SwiftShader every function binds through `vkGetInstanceProcAddr`, with no instance for the global functions and with the one the program created for the rest, since the library exports four symbols. With lavapipe the allocation callbacks also run on the driver's queue thread, which gets musl thread state from NX-08. The whole `tests/xp/` bundle passed on both other hosts with the new fixtures (64 runs on each).

## NX-09: extensions from C headers

The generator reads C headers through the JSON AST of llvmnet's Clang for the portable target (`kind: "headers"` in a description). `tests/extensions/glfw/extension.json` describes GLFW 3.4 (pinned) by its header, `GLFW/glfw3.h` with `GLFW_INCLUDE_NONE`, and the functions named `glfw*`:

| What | Result |
| --- | --- |
| Generation | 119 entry points, all verified against the header as llvmnet compiles it, plus `glfwGetProcAddress`, which returns a host function pointer and fails when called; 23 callback types with reverse thunks; the `GLFWallocator` structure of callbacks copied with thunks, its 32-byte layout asserted; 0.2 s |
| `tests/extensions/glfw.c` on GLFW's null platform, built natively from the same source (CMake, null platform only) | native, portable JIT, NativeAOT and system mode print the same 16 lines: the allocator's callbacks (every allocation freed), the error callback (`0x10003 Invalid window hint`), window, framebuffer and position callbacks with the user pointer, the callbacks the setters return (the program's own again), the null monitor's video mode (1920x1080 at 60 Hz) and gamma ramp, the timer |
| A header with a variadic function and a structure passed by value | the variadic function is left out with the reason; the structure stops the generation (`var_point in variadic.h: struct point is passed by value`) until `exclude` names the function |

A setter's returned callback goes back to the program's function through the thunk pool (`NativeCallbacks.Target`); a host function pointer that is not one of the thunks ends the program with a message.

## NX-07: structures by value

A command that passes or returns a structure by value gets an internal stub that takes the structures by address and a C wrapper with the command's own prototype, compiled into the package as `lib/wrappers.o` and linked into every portable program that uses the extension; the stub hands the host typed CLR structures, so the CLR applies each host's classification. `tests/extensions/structs/` describes, by its header, an API with a pair of floats (8 bytes), an int with a double (16), 17 bytes, four doubles (32), a nested structure (12) and a 3-byte one, as parameters and results; `tests/extensions/structs.c` calls all eleven entry points and two through `dlsym`.

| Host, convention | Native library built with | Builds that print the native output |
| --- | --- | --- |
| Linux x64, System V | clang-22 | native, portable JIT, NativeAOT, system mode (without the calls through `dlsym`, which system mode does not make with structures by value) |
| Linux ARM64, AAPCS64 (Docker with qemu) | clang-22 cross-compiling with Debian's `gcc-aarch64-linux-gnu` | the bundle's portable JIT image; a NativeAOT executable cross-published from Linux x64 |
| macOS ARM64, Apple's AAPCS64 variant | Apple clang 21 on the host | the bundle's portable JIT image and the NativeAOT executable published on the host |
| Windows x64, Microsoft x64 | Visual Studio 2026's `cl` on the host | the bundle's portable JIT image and the NativeAOT executable published on the host |

The four shapes cover the conventions' cases: on x86-64 System V the pair of floats is one SSE register and the int with a double an integer and an SSE register, both of which Windows passes by reference; on ARM64 the pair and the four doubles are homogeneous floating-point aggregates in floating-point registers; 17 bytes go in memory or by reference everywhere. A union by value is rejected (`tests/ext-headers.sh`), as are bit-fields, arrays of structures and function pointers inside a structure passed by value.

## NX-12: installation and distribution

`llvmnet --install-extension <dir>` checks a built package and copies it into the portable sysroot's `ext/`: the manifest, the assembly's `llvmnet.abi`, `llvmnet.version` (by the compatibility rule of managed references) and `llvmnet.extension` (the manifest's name and version), the headers it lists, and names that clash with the sysroot or another installed extension. The copy is staged beside the installed version and renamed over it. `scripts/build-extensions.sh` installs through it and learned to run from an installed toolchain, asking the driver for the version, the runtime assembly, the sysroot and its own location (`llvmnet --print-extension-kit`). Both packages ship `gles` and `vulkan`, `<llvmnet/ext.h>`, the kit (`build-extensions.sh`, `ext-gen.py`) and `templates/native-extension/`.

| What | Result |
| --- | --- |
| `tests/ext-install.sh`: the template (a header with a callback type and a structure passed by value, its library, an example) built with the repository's script | 4 entry points and 1 callback type, 4 stubs verified, `lib/wrappers.o` packaged; the example prints its native output under the JIT, as NativeAOT and in system mode; without the library it prints the names tried and exits 1 |
| `--install-extension` refusals | no manifest (exit 2), an assembly built for llvmnet 0.1.0, an assembly that declares `hello/1.0.0` under a `hello/2.0.0` manifest, a version with a header that shadows `<stdio.h>` and `-lm` (the installed version stays, nothing is left behind); a second `vulkan` that answers `-lGLESv2` beside `gles` |
| `tests/toolchain.sh`: a packaged toolchain, relocated to a path with a space | `--list-extensions` shows `gles` and `vulkan` without problems; the template builds with the package's kit, outside the repository, installs into the relocated sysroot and its example prints the expected output |
| The dotnet tool, packed and installed with `--tool-path` | `gles` and `vulkan` listed, `ext.h` and the kit in the package; the template builds with the tool's kit (`--llvmnet` naming the tool) and its example runs |
| The `xp` bundle's `hello` fixture, the library built by each host's compiler | passed under the JIT and as NativeAOT on Windows x64 (`cl`) and macOS ARM64 (Apple clang), and under the JIT on Linux ARM64 (Docker with qemu) |

The tool package first failed to pack: NuGet refuses the build stamp `ext/<name>/.llvmnet-inputs` (NU5119), which only appears once the release builds the extensions, so the tool's items exclude it. The `xp` runner read its manifest on standard input, and `cmd` building the first native library on Windows consumed the fixtures after it; earlier runs passed because `structs` was the last fixture. The manifest is now read on its own descriptor.

## NX-13: the AvaloniaStatic scenario

Skia from AvaloniaStatic's SkiaSharp 4.153.1 tree (the SkiaSharp C API included) was built with its Ganesh GL backend and EGL twice, in portable mode against the `gles` extension and natively with clang-22 against Mesa, both with `target_cpu="generic"` (1,259 build steps, 74 s and 92 s). `tests/extensions/skia/skia-gl.c` is the renderer: it takes the native handles of a window as arguments, builds Skia's `GrGLInterface` with `gr_glinterface_assemble_gles_interface` from `eglGetProcAddress`, as Avalonia does through SkiaSharp, wraps the window's framebuffer as an `SkSurface`, draws eight frames of an animated UI-like scene (gradients, blurred shadows, round rectangles, a stroked cubic, a clip, a rotation, a scaled bitmap) and reads the last one back before `eglSwapBuffers`. It also draws the same scene with Skia's CPU rasterizer. The host, `tests/extensions/skia/host/`, is a .NET application that creates the window (Xlib, user32 or AppKit), keeps the event loop, and runs the renderer in its own process, as a child process, or (`host-aot/`) compiled with it into one NativeAOT executable. `tests/ext-skia.sh` (tag `optional`, requirements `egl xvfb skia`) runs the Linux forms under Xvfb.

| Host and display | Form | Result |
| --- | --- | --- |
| Linux x64, Xvfb with Mesa llvmpipe | native renderer as a child; portable renderer in the host's process (JIT), as a JIT child, as a NativeAOT child; host and renderer as one NativeAOT executable (14.9 MB) | all five print the same lines (frame `b7f89cd42a6219f0`, raster `d1704f03d5e792c3`, the flat pixels), and the host reads the window back with `XGetImage` as that frame |
| Linux x64, Mesa radeonsi, pbuffer | native and portable JIT | identical frame (`43d00b6323e47beb`) and raster hashes |
| macOS ARM64, ANGLE over Metal (Visual Studio Code's Electron), an `NSWindow` the host created over SSH | portable renderer in the host's process (JIT); host and renderer as one NativeAOT executable published on the Mac | the layer's surface is 960x720 pixels for the 480x360 window (Retina); both forms present eight frames with the same frame hash (`6c344be57edde662`); 257 of 691,200 pixels differ from the raster reference by more than 32 |
| macOS ARM64, ANGLE over Metal, pbuffer | portable JIT | raster hash `d1704f03d5e792c3`, as on Linux; 54 pixels apart from the GPU frame |
| Windows x64, ANGLE over Direct3D 11 (Intel Arc A380), pbuffer | portable JIT | raster hash `d1704f03d5e792c3`, as on Linux; 3 pixels apart from the GPU frame (largest difference 37) |
| Windows x64, a Win32 window the host created over SSH | portable JIT in the host's process; one NativeAOT executable published on Windows | ANGLE cannot create the swap chain: `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE` in a session without a desktop, then `EGL_CONTEXT_LOST`; not run in a desktop session |

The flat pixels the renderer samples are the same on every host and display. The GPU frames differ between drivers only at antialiased edges and in blurs.

Frame times on Linux under Xvfb (llvmpipe, 480x360, one run each of `tests/ext-skia.sh`):

| Form | First frame | Each later frame |
| --- | --- | --- |
| native | 39.7 ms | 0.87 ms |
| portable, JIT in the host's process | 494 ms | 2.01 ms |
| portable, JIT child | 557 ms | 1.93 ms |
| portable, NativeAOT child | 44.3 ms | 1.37 ms |
| portable, one NativeAOT executable with the host | 51.2 ms | 1.25 ms |

On macOS the one NativeAOT executable took 18.7 ms for the first frame and 0.89 ms for each later one at 960x720; the JIT in the host's process 390 ms and 1.21 ms.

What the scenario found:

- **ANGLE's own extensions.** On ANGLE, Skia's GL interface failed to validate. ANGLE advertises `GL_CHROMIUM_bind_uniform_location`, so Skia requires `glBindUniformLocationCHROMIUM`, which is not in the Khronos `gl.xml`, and the extension had no stub for it. `gles` 1.1.0 adds ANGLE's registry (`gl_angle_ext.xml`, `egl_angle_ext.xml` from ANGLE `7e08726`, chromium/7922, the revision AvaloniaStatic builds, pinned in `sources.lock`) through the generator's new `supplements`, with ANGLE's two extension headers: 3,472 entry points, all verified against the headers. This is a minor version under the [versioning policy](../dev/versioning.md#native-extensions).
- **A NativeAOT output directory.** `--nativeaot -o <dir>/<name>` failed with .NET's "Could not find a part of the path" when the directory did not exist, which a JIT image's `-o` creates; the driver now creates it (`tests/driver.sh`).
- **Images as compile-time references.** C# cannot reference a portable image at compile time: the image is built against `System.Private.CoreLib`, and calling `Program.__Entry` fails with CS0012. The one-executable host finds the entry point by reflection over the rooted image instead (NX-14).
