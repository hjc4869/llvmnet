# Native extensions

A portable program is one image for every host, so it cannot link native libraries: the host's libraries differ by operating system and CPU. Some libraries cannot be recompiled into the program either, because they belong to the operating system or the GPU vendor: EGL and OpenGL ES, Vulkan. A *native extension* is how a portable program calls such a library. It is a package of the library's headers and a platform-neutral managed assembly, which loads the host's library when the program first calls it and calls it with the host's calling convention. The program stays one image; what differs per host is only which library the extension finds, and whether it finds one ([ADR-15](../plan/decisions.md#adr-15-native-extensions-of-the-portable-abi)).

| Extension | Host libraries | Link with | Tested on |
| --- | --- | --- | --- |
| `gles`: EGL 1.5, OpenGL ES 2.0 to 3.2, desktop OpenGL and their registered extensions | `libEGL.so.1` and `libGLESv2.so.2` (Linux: Mesa or a vendor driver), `libEGL.dll` and `libGLESv2.dll` (Windows: ANGLE), `libEGL.dylib` and `libGLESv2.dylib` (macOS: ANGLE) | `-lEGL -lGLESv2` or `--extension=gles` | Linux x64 with Mesa (radeonsi and llvmpipe), Windows x64 with ANGLE over Direct3D 11, macOS ARM64 with ANGLE over Metal |
| `vulkan`: Vulkan 1.0 to 1.4 and its registered extensions, except the window-system and provisional ones | `libvulkan.so.1` (Linux: the Khronos loader), `vulkan-1.dll` (Windows: the loader) or `vk_swiftshader.dll`, `libvulkan.1.dylib`, `libMoltenVK.dylib` or `libvk_swiftshader.dylib` (macOS) | `-lvulkan` or `--extension=vulkan` | Linux x64 with Mesa's RADV and lavapipe, Windows x64 with the Intel Arc driver through the loader and with SwiftShader, macOS ARM64 with SwiftShader |

`llvmnet --list-extensions` prints the extensions installed in the toolchain, with their `-l` names, host libraries and headers. `scripts/build-extensions.sh` builds the extensions of a source checkout into `sysroot/dotnet64-v2/ext/`.

## Building a program

An installed extension's headers are on the portable include path, as a `-dev` package's would be, so `#include <EGL/egl.h>` and `#include <GLES3/gl3.h>` need no option. Linking names the extension, through a `-l` name its manifest lists when no archive of that name is found, or explicitly:

```sh
llvmnet-clang -O2 render.c -lEGL -lGLESv2 -o render.dll
llvmnet-clang -O2 render.c --extension=gles --nativeaot -o render
```

A JIT image gets the extension's assembly, `LlvmNet.Ext.Gles.dll`, beside it, as it gets `LlvmNet.Runtime.dll`; NativeAOT compiles it into the executable, and `--aot-project` puts it into the project for another host. The image records the extensions it links in its metadata (`LlvmNet.Checks inspect` prints `llvmnet.extensions: gles/1.0.0`). A program linked with no extension is unchanged.

In system mode an extension names only libraries: `--extension=gles` links `libEGL.so.1` and `libGLESv2.so.2` as `--system-library` does, through generated bridges, so one build configuration serves both modes. The headers are the host's in system mode; on a host without them, `-I <sysroot>/ext/gles/include` uses the extension's.

## At run time

The extension loads a host library when the program first calls one of its functions, opens it with `dlopen` or asks whether it is available. It searches, in order:

1. the directory named by `LLVMNET_EXT_<NAME>_DIR` (`LLVMNET_EXT_GLES_DIR` for `gles`), when it is set;
2. the image's own directory (a NativeAOT executable's directory), so an application can ship the library beside it;
3. the system loader's default path (`LD_LIBRARY_PATH` and the installed libraries on Linux, the application and system directories and `PATH` on Windows, the `dyld` paths on macOS).

A library can name others to load first: `gles` loads `libGLESv2` before `libEGL`, because ANGLE's `libEGL` depends on the `libGLESv2` beside it, which the system loader would not otherwise find there. Each function binds once, on its first call, to the library's export or, for functions the library does not export, to what the API's own lookup function returns (`eglGetProcAddress`), as an application would find it natively.

On Linux, Mesa's `libegl1` and `libgles2` packages (Debian names) are the host libraries. On Windows and macOS, ANGLE is: Chromium, Edge, Electron applications and Firefox ship it, and an application that needs GL ships its own copy beside the image, with ANGLE's license. ANGLE draws through Direct3D 11 on Windows and Metal on macOS.

### Asking whether it will work

`<llvmnet/ext.h>`, part of every llvmnet toolchain in both modes, declares two functions:

```c
#include <llvmnet/ext.h>

if (!llvmnet_ext_available("gles")) {
    fprintf(stderr, "no OpenGL ES on this host:\n%s\n", llvmnet_ext_library("gles"));
    return use_software_renderer();
}
```

`llvmnet_ext_available` returns 1 when the program is linked with the extension and every host library loads and provides the functions the extension requires (for `gles`: `eglGetProcAddress`, `eglGetDisplay`, `eglInitialize`, `eglChooseConfig`, `eglCreateContext`, `eglMakeCurrent`, `eglGetError`, `glGetString` and `glGetError`). `llvmnet_ext_library` says what was loaded, one line per library (`EGL: /usr/lib/x86_64-linux-gnu/libEGL.so.1`), or why not: the files tried and the system loader's own message.

A program can also probe the way it would natively. Some functions have a default implementation that answers when the host has no library: for `gles`, `eglGetDisplay`, `eglGetPlatformDisplay` and `eglGetPlatformDisplayEXT` return `EGL_NO_DISPLAY`, `eglInitialize` and `eglTerminate` return `EGL_FALSE`, `eglGetError` returns `EGL_NOT_INITIALIZED`, `eglQueryString` returns null, the `eglGetCurrent*` functions return null and `eglReleaseThread` returns `EGL_TRUE`; `eglGetProcAddress` returns null. Calling any other function when its library is missing ends the program with one line on standard error and exit status 127, as a native loader does for a missing library:

```
llvmnet: glClear: the host library GLESv2 of the native extension gles is not available (tried /app/libGLESv2.so.2, libGLESv2.so.2; LLVMNET_EXT_GLES_DIR names another directory): libGLESv2.so.2: cannot open shared object file: No such file or directory
```

### The cost of a call

A call through an extension costs about what the host's function costs plus the CLR's transition to native code: `glGetError` on Mesa measured 1.4 to 1.8 ns natively, 3.8 ns from a NativeAOT executable and 5.1 ns under the JIT ([evidence](../evidence/2026-11-03-native-extensions.md#nx-11-the-cost-of-a-call)). Setting `LLVMNET_EXT_GLES_NON_BLOCKING=1` lets the entry points an extension marks non-blocking (`glGetError` and `eglGetError` in `gles`) skip the transition, 2.2 ns in both modes. Set it only when the host library never takes a lock in them: Mesa's do not, but ANGLE's take a lock, and a thread blocked on it while the garbage collector waits for that thread would deadlock a program that calls GL from several threads. The first call of an entry point binds it, about 1.3 µs under NativeAOT; under the JIT it also compiles the stub, about 40 µs.

### Vulkan

`vkGetInstanceProcAddr` and `vkGetDeviceProcAddr` return a pointer exactly when the host's do, for the instance or device they are given; portable code calls it as any function pointer. A function the library does not export binds on its first call through the host's `vkGetInstanceProcAddr` with the instance the program created or asked about last, so SwiftShader or MoltenVK used without the loader serves every function; the loader's functions dispatch through the handle they are given, so one binding serves every instance and device. Application allocation callbacks (`VkAllocationCallbacks`) and debug messengers (`VkDebugUtilsMessengerCreateInfoEXT`, also in the `pNext` chain of a `VkInstanceCreateInfo`, the debug report and device memory report callbacks) reach the portable functions through reverse thunks; the allocation callbacks may run on the implementation's threads. A structure in a `pNext` chain that carries a callback is patched in place for the duration of the call, so such a chain must not be in use by two calls at once; `VkAllocationCallbacks` is copied. The window-system extensions (`VK_KHR_xlib_surface`, `VK_KHR_win32_surface`, `VK_EXT_metal_surface` and the others) are not proxied: the host application creates the `VkSurfaceKHR` with the instance the portable code created and passes the handle in. Without a Vulkan library, `vkCreateInstance` and the `vkEnumerateInstance*` functions return `VK_ERROR_INITIALIZATION_FAILED` and `vkGetInstanceProcAddr` returns null.

### dlopen and dlsym

A program, or a library inside it such as ANGLE or Skia, that loads the API itself finds it through `dlopen`, under the Linux names on every host (portable programs see Linux, [modes.md](modes.md)): for `gles`, `libEGL.so.1`, `libEGL.so`, `libGLESv2.so.2` and `libGLESv2.so`. `dlopen` fails with the reason in `dlerror` when the host library is missing. `dlsym` and `eglGetProcAddress` return a pointer for a function the host provides and null for one it does not, so extension and version checks behave as natively; portable code calls the pointer as any function pointer. One difference: for a name the extension does not know, `eglGetProcAddress` returns null, where libglvnd's returns a dispatch stub for any name that starts with `gl`. A name no extension serves fails as before ([dev/host-abi.md](../dev/host-abi.md#dynamic-loading)).

## What crosses the boundary

The rules are a C ABI boundary's, applied by the CLR for the running host:

- **Integers, floating point and pointers** pass with their exact C types; narrow integers are extended as each host's calling convention requires. Pointers are addresses in the process, so buffers, strings and structures are read and written in place. The APIs an extension proxies use fixed-width and pointer-sized types only, which have the same layout on every 64-bit host.
- **Native handles** are pointer-sized and neutral: the extension's `EGL/eglplatform.h` defines `EGLNativeDisplayType`, `EGLNativeWindowType` and `EGLNativePixmapType` as `void *` on every host. The window system is the host application's: it creates the window (an X11 `Window`, a `wl_egl_window *`, an `HWND`, a `CAMetalLayer *`) and passes its handle to the portable code, which passes it to `eglCreateWindowSurface`.
- **Callbacks** that the API calls, such as `GLDEBUGPROC` for `glDebugMessageCallback`, reach the portable function through a native-callable entry point. Each callback type has a fixed number of them (8 for the GL debug callbacks of `gles`, 4 for the others), so as many distinct functions can be registered at once; registering one again reuses its entry point, and one more ends the program with a message. A callback may run on a thread the host library started itself (asynchronous GL debug output, a driver's worker thread): the first callback on such a thread gives it the program's thread state, so `errno`, `stdio`, `malloc`, thread-local variables, `pthread_self` and pthread keys work there as on the program's own threads. Such a thread cannot be joined or ended with `pthread_exit` from the callback, and its thread-local and key destructors do not run; its state is released after the thread ends.
- **Not proxied**: structures passed by value (NX-07; EGL and GL have none), variadic functions, C++ interfaces, and types whose size differs between hosts. In `gles`, the 23 `GL_ARB_shader_objects` functions that use `GLhandleARB` (an `unsigned int` on Linux and Windows but a pointer on macOS) are left out; use the core GL 2.0 equivalents. `glGetVkProcAddrNV` returns a host function pointer, which portable code cannot call, so calling it ends the program.
- **errno** is the host library's own, not the program's.

## Shipping a program

A JIT image's directory holds the image, `LlvmNet.Runtime.dll` and each extension's assembly; a NativeAOT executable is one file. Neither contains the host library: it is the host's, or the application's to ship beside the image. On Windows and macOS that means shipping ANGLE (`libEGL.dll` and `libGLESv2.dll`, or the `.dylib` pair) next to the program or the executable, or naming its directory with `LLVMNET_EXT_GLES_DIR`. [deployment.md](deployment.md) lists what each output form needs.

The host library runs as native code with the process's rights, like the program itself ([SECURITY.md](../../SECURITY.md)); an extension loads only the file names its manifest lists, from the directories above.

## Writing an extension

An extension is generated from the API's registry or headers by `scripts/ext-gen.py` and checked against the C prototypes before it is installed; [dev/extensions.md](../dev/extensions.md) describes the description file, the generated assembly and the checks.
