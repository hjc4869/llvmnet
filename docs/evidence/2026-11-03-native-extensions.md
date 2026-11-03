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
