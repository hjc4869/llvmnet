# Writing a native extension

A native extension lets a portable program call a host library that cannot be recompiled into portable bitcode, such as EGL and OpenGL ES, while the program stays one image for every host ([ADR-15](../plan/decisions.md#adr-15-native-extensions-of-the-portable-abi)). [user/extensions.md](../user/extensions.md) describes using one; this page describes how one is made. An extension is generated, not written by hand: a description names the API's registry, the host libraries and a few rules, and `scripts/ext-gen.py` writes the stubs, the headers and the package. `scripts/build-extensions.sh` builds, checks and installs it.

## Sources

Each extension shipped with llvmnet is a directory `extensions/<name>/`:

- `extension.json`, the description below.
- The extension's own headers, such as the neutral `include/EGL/eglplatform.h` of `gles`, which replaces the registry's platform header.
- `licenses/`, the notices the package must carry.

The registries and the headers they come with are pinned sources ([scripts/sources.lock](../../scripts/sources.lock), fetched by `scripts/fetch-sources.sh`), never copied into the repository. The `gles` extension uses `egl-registry` and `opengl-registry`.

## The description

| Key | Meaning |
| --- | --- |
| `name`, `version` | The extension's name (lowercase letters, digits and dashes; it names the package directory, `--extension=<name>`, `LlvmNet.Ext.<Name>.dll` and `LLVMNET_EXT_<NAME>_DIR`) and its version, which is independent of llvmnet's and of `dotnet64-v2` |
| `summary` | One line for the package manifest |
| `registries` | Each registry: `path` (`<source>:<path>` in a pinned source, or a path relative to the description), the `apis` whose features and extensions select its entry points (`egl`; `gles2` and `gl`), and the `library` that implements them. For an API without a registry, `kind: "headers"` reads C headers instead: `include-path` and `header` (`glfw:include`, `GLFW/glfw3.h`), the `defines` to compile them with, and the `include` and `exclude` name patterns of the functions to proxy (`glfw*`) |
| `libraries` | Each host library, in load order: `name`, its file names per system (`linux`, `windows`, `macos`; the first one of each goes to the package manifest, where system mode links the Linux one), the names `dlopen` serves (`dlopen`; programs use their Linux names, `libEGL.so.1`) and the libraries to load first (`preload`) |
| `link` | The `-l` names the extension answers (`EGL`, `GLESv2`) |
| `lookup` | The API's own lookup function (`eglGetProcAddress`): its stub returns stubs, and stubs of functions the host library does not export bind through the host's copy of it. With `stub` and `bind`, both methods of the extension's own `sources`, the lookup function's stub forwards to `stub` and binding asks `bind` (Vulkan's `vkGetInstanceProcAddr` takes an instance) |
| `replace` | Entry points whose stub forwards to a method of the extension's own `sources` (`vkGetDeviceProcAddr`) |
| `after` | Entry points followed by a call of a method of the extension's own `sources`, with the result and the arguments (`vkCreateInstance`, so that binding knows the instance) |
| `sources` | C# files compiled into the assembly with the generated code |
| `platforms`, `provisional`, `exclude-extensions` | Registry extensions for a window system (`platform=` in `vk.xml`) are left out unless listed, provisional ones unless `provisional` is true, and those named are left out |
| `types` | The meaning of types a platform header defines, as C text (`EGLNativeDisplayType`: `void *`) |
| `exclude-types` | Types whose size differs between hosts and that the extension leaves out, with every entry point that uses them (`GLhandleARB`, `void *` on macOS and `unsigned int` elsewhere) |
| `exclude` | Entry points to leave out by name |
| `callbacks` | The callback types that get reverse thunks, each with its pool size: the number of distinct portable functions that can be passed as such a callback |
| `defaults` | The value an entry point returns when the host does not provide it, instead of ending the program (`eglGetDisplay`: `0`, which is `EGL_NO_DISPLAY`) |
| `required` | The entry points `llvmnet_ext_available` requires the host to provide |
| `non-blocking` | Entry points that may skip the GC transition when a run sets `LLVMNET_EXT_<NAME>_NON_BLOCKING=1`: only functions that, in every host library the extension may load, take no lock, never block and never call back (`glGetError` in Mesa; ANGLE takes a lock in it, which is why the switch is the user's) |
| `headers` | The public headers: `from` (a pinned source's directory or the extension's own) and the `files` to copy, by their include names |
| `licenses` | The notices to install under `share/licenses/<name>/`: `from` and the name to install it `as` |
| `checks` | The C files of the signature check (and `lib/wrappers.o`, when the extension has wrappers): each `name`, the `apis` whose entry points it declares, the `defines` and the `includes` (a set of headers that can be included together; GLES and desktop GL headers cannot); `types: false` leaves the registry's type definitions out when the headers define them all, and `layouts: true` asserts the layouts the structure patching uses |

## C headers

A registry of `kind: "headers"` is read through the JSON AST that llvmnet's Clang produces for the portable target (`llvmnet -fsyntax-only -Xclang -ast-dump=json`, run by the generator with `--compiler`), so the types are those portable programs see: the external functions whose names match `include` and not `exclude`, their parameter types as Clang prints them, typedefs (function pointer typedefs are callback types), records with their fields and enums. Variadic functions are left out and reported; a function whose types the portable ABI cannot proxy stops the generation with the reason, until `exclude` names it. The check file includes the headers and takes every function's address; it has no registry text to declare them from. `tests/ext-headers.sh` validates this on GLFW 3.4.

## What is generated

`ext-gen.py generate` writes into `artifacts/ext-build/<name>/generated/`:

- `project/Generated.cs` and the project file of `LlvmNet.Ext.<Name>.dll`, which references `LlvmNet.Runtime` and declares `llvmnet.abi = dotnet64-v2`, `llvmnet.version`, `llvmnet.extension = <name>/<version>` and the registries' hashes.
- `package/`: `manifest.json` in the form the driver reads ([driver.md](../user/driver.md#link-inputs)), `include/` and `share/licenses/`.
- `check/<group>.c` and `signatures.json`, for the checks below.

The assembly holds:

- **A stub per entry point**, a public static method with `[CExport("<name>")]` whose CIL signature is llvmnet's mapping of the C prototype (integers of up to 32 bits `int`, 64-bit integers `long`, pointers `nint`, `float`, `double`). On its first call it binds to the host's function, the export of its library or else what the lookup function returns, and keeps the address in a static field. It calls the function through `delegate* unmanaged<...>` with the exact C types, so the CLR applies the host's calling convention: on Apple ARM64 the caller extends narrow integers, on SysV and Win64 the rules differ again, and the stub states only the C types. Narrow results are returned zero-extended, as translated code keeps them.
- **The table**, every entry point's stub by name, which `dlsym` and the lookup function's stub return. A lookup returns a stub only when the host provides the function, so extension and version checks behave as natively.
- **Non-blocking calls.** A stub of a `non-blocking` entry point calls through a second slot with `delegate* unmanaged[SuppressGCTransition]`, which binding fills only when the switch is on; the ordinary call is a method of its own, because the JIT sets up the transition frame in the prologue of any method that contains one, and NativeAOT's compiler merged two calls of one pointer that differed only in the transition.
- **Callbacks inside structures.** A parameter that points to a struct with function-pointer members of a type in `callbacks` (`VkAllocationCallbacks`) gets a copy with reverse thunks in their place; a parameter whose struct's `pNext` chain may carry such structs (the `structextends` of `vk.xml`: a `VkDebugUtilsMessengerCreateInfoEXT` in a `VkInstanceCreateInfo`) has those nodes patched in place, found by their `sType`, and restored after the call (`CallbackPatch`). The offsets come from the registry's member lists in the portable layout, and the check file asserts them against the headers.
- **Structures by value** (NX-07). A command that passes or returns a structure by value gets an internal stub, `__llvmnet_ext_<name>_<function>`, that takes the structures by address (and the result's storage first), and a C wrapper with the command's own prototype, in `lib/wrappers.o`, which the driver links into every portable program that uses the extension. The stub calls the host with typed CLR structures (`Structs`, sequential layout with the members' exact types, fixed buffers for arrays), so the CLR classifies them by the host's convention. The wrappers register themselves from a constructor (`__llvmnet_ext_register`), and `dlsym` and the lookup function hand them out. Unions, bit-fields, arrays of structures and function pointers inside a structure passed by value are rejected; `long double` and `long` are rejected anywhere.
- **Returned callbacks.** An entry point that returns a callback type in `callbacks` (GLFW's setters return the previous callback) returns the program's function when the host returns one of the extension's thunks, null for null, and ends the program for any other host function pointer.
- **Reverse thunks** for each callback type in `callbacks`: a pool of `[UnmanagedCallersOnly]` methods that call the portable function stored in their slot, converting the C arguments back to llvmnet's forms. The same portable function always gets the same thunk. A thunk first calls `NativeCallbacks.Enter`, which gives a thread the host library started musl's thread state ([host-abi.md](host-abi.md#dynamic-loading)).
- **The registration class**, the one public `NativeExtension` subclass the image initializer registers, which tells `dlopen`, `dlsym` and `<llvmnet/ext.h>` about the host libraries.

The runtime support it uses is in `src/LlvmNet.Runtime/HostLibraries.cs`: `HostLibrary` finds a library (in `LLVMNET_EXT_<NAME>_DIR`, beside the image, then on the system loader's path, after its preloads) and remembers why it could not; `HostEntryPoints` binds stubs and answers availability; `NativeCallbacks` hands out thunk slots; `NativeExtension.Fail` ends the program with one `llvmnet:` line and exit status 127, as a native loader does when a library or a lazily bound function is missing.

## Types

| C type | Stub (CIL) | Host call |
| --- | --- | --- |
| `char`, `signed char`, `int8_t`, `khronos_int8_t` | `int` | `sbyte` |
| `unsigned char`, `uint8_t`, `khronos_uint8_t` | `int` | `byte` |
| `short`, `int16_t` / `unsigned short`, `uint16_t` | `int` | `short` / `ushort` |
| `int`, `int32_t` / `unsigned int`, `uint32_t` | `int` | `int` / `uint` |
| `long long`, `int64_t` / `unsigned long long`, `uint64_t` | `long` | `long` / `ulong` |
| `intptr_t`, `ptrdiff_t`, `ssize_t`, `khronos_intptr_t`, `khronos_ssize_t` / `uintptr_t`, `size_t` | `long` | `nint` / `nuint` |
| `float`, `double` | `float`, `double` | the same |
| any pointer, array parameter or opaque handle | `nint` | `nint` |
| a callback type in `callbacks` | `nint` | `nint` of the thunk |

The `khronos_*` types are mapped by name, by what they mean on every host, not by the C spelling of the build host's `khrplatform.h`. Rejected, with the entry point and the reason: `long` and `unsigned long` (32 bits on Windows), `long double`, `wchar_t`, `bool`, 128-bit integers, structures passed by value (NX-07), and types the description gives no meaning. Registries have no variadic entry points. An entry point that returns a host function pointer, other than the lookup function, gets a stub that ends the program with a message, because portable code can call only managed pointers; a callback parameter of a type without thunks passes null and ends the program for anything else.

## Checks

`scripts/build-extensions.sh` checks every stub before it installs the package:

1. llvmnet compiles each check file: the headers of the group, the registry's type definitions and a declaration of every entry point from the registry's own C text. A declaration that contradicts a header is a compile error.
2. `ext-gen.py verify` compares the declarations in the compiled IR with the stubs, including the `signext` and `zeroext` of narrow integers, which decide the signedness of the host call.
3. The check objects, whose address tables refer to every entry point, are linked against the assembly with `--cil-reference`, and the compiler checks each stub's CIL signature as it does for any managed reference.

A mistake in the generator or a type in `types` therefore fails the extension's build, not a user's program.

## Building and testing

```sh
scripts/build-extensions.sh gles                     # into sysroot/dotnet64-v2/ext/gles
scripts/build-extensions.sh --sysroot <dir> <path/to/extension.json>
```

The script rebuilds an extension only when an input changes: its directory, the generator, the script, the pinned sources, `LlvmNet.Runtime` and llvmnet's version. `tests/ext-gen.sh` builds the synthetic registry in `tests/extensions/generated/` and runs a program natively, in portable mode (JIT and NativeAOT) and in system mode against host libraries it compiles with clang, and checks the generator's rejections. `tests/ext-gles.sh` runs the `gles` extension against the host's Mesa.

## Adding an extension

1. Pin the registry in `scripts/sources.lock`, with the headers that come with it.
2. Write `extensions/<name>/extension.json`; replace platform headers whose types differ by host with neutral ones of the extension's own.
3. Run `scripts/build-extensions.sh <name>` until it passes; read the generator's list of left-out and unsupported entry points.
4. Add a test that runs a program against the host library, and the notices to THIRD-PARTY-NOTICES.md.
