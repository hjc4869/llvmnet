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
