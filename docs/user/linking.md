# Linking: bitcode, managed and native libraries

llvmnet has three kinds of library: archives of LLVM bitcode objects (the ordinary C way), managed libraries that other .NET code or other llvmnet programs call, and native shared libraries: in system mode directly, in portable mode through native extensions. [driver.md](driver.md#link-inputs) states the rules the linker applies to objects and archives; this page is about what each kind is for.

## Bitcode objects and archives

`llvmnet -c` writes an object that is LLVM bitcode tagged with the runtime mode's ABI; `llvmnet-ar` (LLVM's `llvm-ar`) makes archives of them, and `llvmnet-nm` and `llvmnet-ranlib` work on them. A link takes objects and archives in any order, extracts the archive members that resolve referenced symbols (rescanning until nothing more resolves), and fails on a duplicate definition naming both files. The whole program, runtime libraries included, becomes one bitcode module, which is translated as a unit: there is no separate compilation at the CIL level, and a program's internal functions are not visible outside it.

```sh
llvmnet-clang -O2 -c module.c -o module.o
llvmnet-ar rcs libmodule.a module.o
llvmnet-clang main.c -L. -lmodule -o program.dll
```

Inputs must come from one mode: a portable link rejects a system-mode object (`ABI mismatch: ... was compiled for system-linux-x64-v1, but this link is dotnet64-v2`) and untagged LLVM IR (`llvmnet --runtime=<mode> -x ir -c` tags IR).

## Managed libraries

`-shared` (or `--emit-library`) produces a CIL library without an entry point. Its public surface is the set of `CExport` methods: one static method per external, default-visibility C function, with the C name, so `-fvisibility=hidden` and `__attribute__((visibility("default")))` select the exports as for a native shared library. Other llvmnet programs reference it with `--cil-reference`, which resolves calls by name and checks the library's ABI tag and compiler version:

```sh
llvmnet-clang -shared -fvisibility=hidden exports.c -o Exports.dll
llvmnet-clang consumer.c --cil-reference Exports.dll -o consumer.dll
```

Scalar and pointer signatures (`int`, `long`, `float`, `double`, pointers) are the stable surface for 1.0 ([ADR-09](../plan/decisions.md#adr-09-portable-abi-evolution)); aggregate parameters and results across assemblies are not yet a stable ABI. .NET code can call the exports too: they are ordinary `public static` methods on the `Program` class, taking pointers as `nint`, and the library carries `LlvmNet.Runtime.CExportAttribute` with each C name (`LlvmNet.Checks inspect Exports.dll` lists them). C# reaches them through reflection for now: the library is built against `System.Private.CoreLib`, so a C# project that references it and calls `Program.add` directly fails with CS0012 ([NX-14](../plan/11-native-extensions.md)).

A portable library does not contain the C library: musl's process state (the thread pointer, the heap, stdio) belongs to the program image, so a library that calls the C library must be linked into a program as bitcode, or the program must be built in the same mode and provide it. A library built from several objects links them together as a program does.

## Native libraries (system mode)

In system mode every function the bitcode does not define resolves in the host's C library, libm, libgcc_s or a library named with `--system-library`:

```sh
llvmnet-clang --runtime=system client.c --system-library /path/libexternal.so -o client.dll
```

LLVM generates a bridge for each imported signature (`lib<name>.llvmnet-host.so` next to a JIT image, linked statically into a NativeAOT executable): scalars, structures by value, `sret`, varargs and native data all work, and C callbacks passed to native code are native-callable entry points as long as they take and return scalars. The library stays a dynamic dependency, found through the loader's usual path (`LD_LIBRARY_PATH` or an installed location); the compiler host must be able to load it too, because the symbols are resolved at compile time. Native C++ libraries are not callable through their C++ ABI: provide a C wrapper, or compile the C++ code to bitcode. Portable mode rejects `--system-library`: a dependency is recompiled in portable mode or reached through a managed interface.

## Native objects and assembly (system mode)

System mode links a project's assembled code into the image's native library (`lib<name>.llvmnet-host.so`, or NativeAOT's static link): native object files on the command line, recognized by their content (ELF, COFF or Mach-O, whatever their names), the native members of archives that also hold the project's bitcode, as ffmpeg's do when nasm assembles its kernels, and assembly sources, which llvmnet assembles itself: `.s`, `.S` and `-x assembler` or `assembler-with-cpp` with Clang for x86-64 Linux and the command line's `-I`, `-D` and target options, `.asm` with nasm (`-f elf64`, the `-I` and `-D` options). `-c` on an assembly source writes its native object.

```sh
llvmnet-clang --runtime=system -O2 main.c kernels.S dsp.asm -o program.dll
llvmnet-clang --runtime=system -c kernels.S -o kernels.o      # a native object, for an archive or a later link
```

Translated code calls the objects' functions through the same bridges as system libraries' functions, function pointers to them included. The data they share with the program live in the native library: a variable the program defines and the assembly reads (C constant tables) is stored there, with the program's initializer, and a variable the assembly defines is the program's to read and write there. A native object that calls a function of the program, rather than receiving it as a function pointer, is a compile error naming the object and the function, as is a symbol that neither the objects, the program nor a system library defines. Every native member of an archive is linked, not only those that resolve a reference. A NativeAOT build from a kept `.bc` (`--keep-ir`) needs the objects or archives again on its command line; their bitcode members resolve nothing more there. Portable mode does not link native objects: it calls assembly through a [native kernel package](native-kernels.md), and rejects assembly sources and native objects otherwise, naming them.

## Native extensions (portable mode)

A host library that cannot be recompiled, because it is the operating system's or the GPU vendor's (EGL and OpenGL ES), is reached through a native extension: a package under the sysroot's `ext/` with the library's headers, which are on the include path, and a generated managed assembly that the link references as it references a managed library. `-l<name>` links the extension whose manifest lists the name when no archive matches, and `--extension=<name>` names it; `llvmnet --list-extensions` lists the installed ones.

```sh
llvmnet-clang render.c -lEGL -lGLESv2 -o render.dll    # LlvmNet.Ext.Gles.dll is copied beside render.dll
```

The assembly loads the host's library at run time under its name for the running system, so the image stays portable; `dlopen` serves the library under its Linux names, and a program asks whether it is available with `<llvmnet/ext.h>`. In system mode the same options link the extension's Linux libraries through generated bridges. [extensions.md](extensions.md) describes the run-time search, availability and what crosses the boundary.

## Build-system integration

The CMake toolchain file (`cmake/llvmnet.cmake`, installed as `share/llvmnet/cmake/llvmnet.cmake` with `Platform/LlvmNet.cmake` and `llvmnet-rules.cmake` beside it) and the Meson cross file (`meson/llvmnet.ini`, installed as `share/llvmnet/meson/llvmnet.ini`) set the compilers and the archiver, and run the produced executables for configure probes. Autotools projects take `CC=llvmnet-clang CXX=llvmnet-clang++ AR=llvmnet-ar RANLIB=llvmnet-ranlib NM=llvmnet-nm`. `scripts/build-dependencies.sh` is the worked example: zlib, libxml2 and dav1d from pinned sources through their own build systems, then ffmpeg ([dev/ffmpeg.md](../dev/ffmpeg.md)).

### Configure probes

Probes get the answers a native Clang build gets, in both modes, because they link real programs and run them where they need to:

- **Function probes.** autoconf's `AC_CHECK_FUNC` and `AC_CHECK_LIB`, Meson's `has_function`, CMake's `check_function_exists` and ffmpeg's `check_func` declare the function without its parameters (`char f(void);`, or `int f();`) and link a call without arguments. Natively that call links whatever the definition is. llvmnet compiles it to a run-time trap and prints a warning ([driver.md](driver.md#calls-that-contradict-their-definition)), so the probe links exactly when the function exists; a function that exists nowhere fails the link.
- **CMake probes are linked.** `check_function_exists`, `check_library_exists` and `check_c_source_compiles` fail for code that does not link, and `try_run` and `check_c_source_runs` run the program.
- **Strings read out of a probe.** CMake identifies a compiler by reading strings out of the files its probes compile, which it cannot do with bitcode objects, so the toolchain file states what those probes would find: Clang of the bundled LLVM version, 8-byte little-endian pointers, `gnu17` and `gnu++17` by default. `check_type_size`, CMake's ABI check and `FindOpenMP` read `INFO:` strings out of the program a probe links; in those probe projects the toolchain copies the strings from the managed image into the launcher, where CMake finds them.
- **Cost.** A probe that links costs about 0.25 s, the time of a link and its translation, so a configure takes longer than natively: zlib 3.9 s against 0.5 s, LibRaw's autotools configure 9 s against 2.4 s, ffmpeg's 56 s against 9 s.
- **musl is not glibc.** A probe for something musl does not have fails as it does in a native build against musl: libxml2 finds no `getentropy` in `<sys/random.h>`, and autoconf's C23 check fails for want of `nullptr_t`. libtool's `rm -f conftest*` cannot remove the launcher's `conftest.llvmnet` directory and says so during configure, which changes no result.

### The CMake platform

The toolchain file selects its own CMake platform, `LlvmNet` (`CMAKE_SYSTEM_NAME`), rather than `Linux` or `Generic`:

- **`UNIX` is set.** Programs see a POSIX system, so `FindIconv`, `FindThreads` and project `if(UNIX)` branches take their Unix paths, and objects end in `.o`.
- **No host directories are searched.** The host's libraries are native code, which a bitcode link cannot use, so name the prefixes of dependencies with `CMAKE_PREFIX_PATH` or `<Package>_ROOT`.
- **`SHARED` and `MODULE` libraries are archives.** A CIL library cannot contain the C library ([above](#managed-libraries)), so such a target becomes an archive of bitcode under its Linux names (`libz.so.1.3.2`, `libz.so.1`, `libz.so`). It holds the target's objects and the members of the static libraries it links, which a native shared library would contain, and programs link it like any archive. Its shared dependencies are named again where it is linked. A project that builds a shared and a static library with one output name (zlib, libjpeg-turbo), and tests that link the shared one, build unchanged. A CIL library for other .NET code is built with the driver's `-shared`.
- **C++20 module scanning is off** (`CMAKE_CXX_SCAN_FOR_MODULES`), because `clang-scan-deps` cannot read llvmnet's command lines.

### Runtime mode

`-DLLVMNET_RUNTIME=system` selects system mode; `portable` is the default. The toolchain file makes the mode an argument of the compiler command itself (`CMAKE_C_COMPILER` is `llvmnet-clang;--runtime=system`), so it reaches every compile, link and probe, also in a directory that resets `CMAKE_C_FLAGS`, as zlib's tests do. When `-DCMAKE_C_COMPILER` names another driver, give the mode in that list (`'-DCMAKE_C_COMPILER=llvmnet-clang;--runtime=system'`). System-mode C++ also needs the optional libstdc++ archive ([modes.md](modes.md#optional-system-c-library)). The environment variable `LLVMNET_RUNTIME=system` or `portable` selects the mode for the scripts in `scripts/`.
