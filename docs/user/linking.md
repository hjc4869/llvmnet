# Linking: bitcode, managed and native libraries

llvmnet has three kinds of library: archives of LLVM bitcode objects (the ordinary C way), managed libraries that other .NET code or other llvmnet programs call, and, in system mode, native shared libraries. [driver.md](driver.md#link-inputs) states the rules the linker applies to objects and archives; this page is about what each kind is for.

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

Scalar and pointer signatures (`int`, `long`, `float`, `double`, pointers) are the stable surface for 1.0 ([ADR-09](../plan/decisions.md#adr-09-portable-abi-evolution)); aggregate parameters and results across assemblies are not yet a stable ABI. .NET code can call the exports too: they are ordinary `public static` methods on the `Program` class, taking pointers as `nint`, and the library carries `LlvmNet.Runtime.CExportAttribute` with each C name (`LlvmNet.Checks inspect Exports.dll` lists them).

A portable library does not contain the C library: musl's process state (the thread pointer, the heap, stdio) belongs to the program image, so a library that calls the C library must be linked into a program as bitcode, or the program must be built in the same mode and provide it. A library built from several objects links them together as a program does.

## Native libraries (system mode)

In system mode every function the bitcode does not define resolves in the host's C library, libm, libgcc_s or a library named with `--system-library`:

```sh
llvmnet-clang --runtime=system client.c --system-library /path/libexternal.so -o client.dll
```

LLVM generates a bridge for each imported signature (`lib<name>.llvmnet-host.so` next to a JIT image, linked statically into a NativeAOT executable): scalars, structures by value, `sret`, varargs and native data all work, and C callbacks passed to native code are native-callable entry points as long as they take and return scalars. The library stays a dynamic dependency, found through the loader's usual path (`LD_LIBRARY_PATH` or an installed location); the compiler host must be able to load it too, because the symbols are resolved at compile time. Native C++ libraries are not callable through their C++ ABI: provide a C wrapper, or compile the C++ code to bitcode. Portable mode rejects `--system-library`: a dependency is recompiled in portable mode or reached through a managed interface.

## Build-system integration

The CMake toolchain file (`cmake/llvmnet.cmake`, installed as `share/llvmnet/cmake/llvmnet.cmake`) and the Meson cross file (`meson/llvmnet.ini`, installed as `share/llvmnet/meson/llvmnet.ini`) set the compilers, the archiver and the executable suffix, and run the produced executables for configure probes. CMake identifies a compiler by reading strings out of the files its probes compile, which it cannot do with bitcode objects and launchers, so the toolchain file states what those probes would find: Clang of the bundled LLVM version, 8-byte little-endian pointers, `gnu17` and `gnu++17` by default. Probes that read a compiled file in the same way cannot: `check_type_size` reports the type as present with an empty size, so set such results in the cache or test them with `check_c_source_runs`. Probes that compile, link or run a program (`check_include_file`, `check_symbol_exists`, `check_c_source_compiles`, `check_c_source_runs`) work. Pass `-DCMAKE_C_FLAGS=--runtime=system` (and the same for C++) for system mode; system-mode C++ also needs the optional libstdc++ archive ([modes.md](modes.md#optional-system-c-library)). `LLVMNET_RUNTIME=system` or `portable` selects the mode for the scripts in `scripts/`, and `scripts/build-dependencies.sh` is the worked example: zlib, libxml2 and dav1d from pinned sources through their own build systems, then ffmpeg ([dev/ffmpeg.md](../dev/ffmpeg.md)).
