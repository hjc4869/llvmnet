# Driver reference

`llvmnet` is the compiler driver: it runs Clang or Flang to produce LLVM bitcode, links the bitcode, and translates the result to a .NET assembly, a launcher or a NativeAOT executable. Its command line follows Clang's, so build systems configured for `clang` work with `CC=llvmnet-clang`, `CXX=llvmnet-clang++` and `FC=llvmnet-flang` ([cmake/llvmnet.cmake](../../cmake/llvmnet.cmake) and [meson/llvmnet.ini](../../meson/llvmnet.ini) do that). This page is the reference for what the driver accepts; the option tables are generated from the driver's own option table and checked by `tests/driver-reference.sh`, and `tests/driver-options.sh` feeds every option Clang's and Flang's `--help` list to the driver in both modes.

## Entry points

| Command | Frontend |
| --- | --- |
| `llvmnet` | Clang for `.c`, `.i`, `.ll` and `.bc`; Clang's C++ mode for `.cc`, `.cpp`, `.cxx`, `.C` and `.ii`; Flang for `.f`, `.for`, `.f90`, `.f95`, `.f03` and `.f08` |
| `llvmnet-clang` | Clang |
| `llvmnet-clang++` | Clang in C++ mode (`--driver-mode=g++`), whatever the input's extension |
| `llvmnet-flang` | Flang |

`--driver-mode=g++`, `--driver-mode=flang` and `--driver-mode=gcc` select the frontend without the aliases, as in Clang. `-x <language>` names the language of the inputs that follow it.

## Steps

A build has the same steps as with Clang, and the same options select them:

1. **Compile** (`-c`): each source becomes an LLVM bitcode object (`.o`), tagged with the ABI of the runtime mode. `-S -emit-llvm` writes textual IR instead. `-E`, `-M`, `-MM` and `-fsyntax-only` run the frontend alone.
2. **Link**: objects, archives of bitcode objects (`-l`, `-L`, or their paths) and the runtime libraries of the mode are linked into one module. `-emit-llvm` or `-r` stops here and writes the module.
3. **Translate**: the linked module is lowered and translated to CIL. The output name chooses the form:

| Output | Result |
| --- | --- |
| `-o program.dll` | CIL assembly, with `program.runtimeconfig.json` and a copy of `LlvmNet.Runtime.dll` next to it; run with `dotnet program.dll` |
| `-o program` (or no `-o`: `a.out`) | A Unix launcher script plus a `program.llvmnet/` payload directory |
| `-shared -o Library.dll` | A CIL library without an entry point, for `--cil-reference` |
| `--nativeaot -o program` | A native executable published with NativeAOT; `--aot-project <dir>` writes the project for another host instead |
| `-emit-llvm -o program.bc`, `-r -o combined.o` | The linked bitcode |

Keep an emitted assembly with the files written next to it: its `runtimeconfig.json`, `LlvmNet.Runtime.dll` and, in system mode, the native bridge `lib<name>.llvmnet-host.so`. [deployment.md](deployment.md) describes what each form needs on the host.

## Defaults

- **Runtime mode**: `--runtime=portable` ([modes.md](modes.md)).
- **Optimization**: `-O1` with automatic vectorization disabled (`-fno-vectorize -fno-slp-vectorize`), decided in [ADR-11](../plan/decisions.md#adr-11-driver-defaults). This differs from Clang, whose default is `-O0`: unoptimized IR is much larger as CIL and slower to JIT-compile, and build systems pass an explicit level anyway. Any `-O` option on the command line replaces the level; `-fvectorize` or `-fslp-vectorize` turns vectorization back on, and the vector IR is scalarized to CIL. `-O0` through `-O3`, `-Os` and `-Oz` are all accepted; `-O0` keeps every function unoptimized (`optnone`), and `-O3` is exercised by the SPEC runs and the fixture tests.
- **Language standard**: the frontends' own defaults (Clang 22: `gnu17` for C, `gnu++17` for C++; Flang: Fortran 2018 with extensions). `-std=` overrides.
- **Stack protector**: `-fno-stack-protector` is passed for C and C++, because CIL has no stack canaries.
- **Debug information**: none is emitted into assemblies yet; `-g` and `-gline-tables-only` give compiler diagnostics `file:line:col` positions.

## How options are treated

Every option falls into one of four classes, per runtime mode:

- **Handled**: llvmnet implements it. These are the steps, outputs, link inputs, runtime modes, NativeAOT and diagnostic options in the tables below.
- **Passed to the frontend**: everything else that starts with `-` goes to Clang or Flang unchanged and in order, including a value given as the next argument (the driver knows the frontends' option spellings from their own `--help`). The frontend reports unknown options and invalid values itself, with its exit status.
- **Accepted without effect**: options that build systems pass for native toolchains and that have no meaning for CIL (`-fPIC`, `-Wl,-rpath`, `-static` in portable mode, `-pthread` in portable mode, ...). `-v` prints a note with the reason for each one.
- **Not accepted**: options that would change the portable ABI (`-m<option>`, `--target=`, `-fshort-wchar`, ...) in portable mode, native linker options with no CIL equivalent, and sanitizer or profiling instrumentation whose runtime is not available. Each fails with `llvmnet: error: option '<option>' is not accepted: <reason>` and exit status 2.

`-Wl,` options are split at commas and classified one by one; `-Xlinker` passes one option the same way. In the tables, "portable: ...; system: ..." marks the options whose treatment depends on the mode.

<!-- generated by llvmnet --print-driver-reference; do not edit -->
### Steps and outputs

| Option | Effect |
| --- | --- |
| `-c` | Compile each input to an LLVM bitcode object (.o) and stop; .ll, .bc and .o inputs are tagged for the mode |
| `-S` | With -emit-llvm, write textual LLVM IR (.ll) instead of an object |
| `-E` | Preprocess only, with the mode's headers |
| `-M` | Write a dependency file (also -MM, -MD, -MMD, -MF, -MT, -MQ, -MP, -MG, -MV, -MJ as in Clang) |
| `-fsyntax-only` | Parse and check the inputs without producing output |
| `-emit-llvm` | Stop after linking and write the linked bitcode (default a.bc) |
| `-r` | Link to a relocatable bitcode object (default a.o) instead of an image |
| `-shared` | Emit a CIL library without an entry point (default a.dll), not a native shared object |
| `-o <file>` | Output: a .dll is a CIL assembly with its runtimeconfig.json and LlvmNet.Runtime.dll; any other name is a Unix launcher plus a <name>.llvmnet/ payload, or a native executable with --nativeaot (default a.out) |
| `-x <language>` | Language of the inputs that follow, as in Clang (c, c++, ir, f95, ...); .ll and .bc inputs are ir by extension |
| `-###` | Print the frontend commands instead of running them, and stop before linking |

### Inputs and libraries

| Option | Effect |
| --- | --- |
| `-L <dir>` | Directory searched for -l archives |
| `-l <name>` | Link lib<name>.a, an archive of LLVM bitcode objects (-l:<file> names the file exactly); -lc, -lm, -lpthread, -ldl, -lrt and, in portable mode, -lc++ and the Fortran runtime names need no archive |
| `-nostdlib` | Portable: link neither the start object nor the sysroot libraries; system: no Fortran runtime |
| `-nodefaultlibs` | Portable: link no sysroot library (musl, libc++, flang-rt, OpenMP, builtins); system: no Fortran runtime |
| `-nostartfiles` | Portable: do not link musl's start object crt1.o |
| `-nostdlib++` | Portable: do not link libc++ and libc++abi |
| `-Wl,<option>,...` | Linker options; the accepted ones are listed under "-Wl, and -Xlinker options" |
| `-Xlinker <option>` | One linker option, as -Wl,<option> |

### Runtime mode and ABI

| Option | Effect |
| --- | --- |
| `--runtime=<mode>` | portable (the default; ABI dotnet64-v2, runs on every supported .NET host) or system (the native Linux x86-64 ABI, system-linux-x64-v1) |
| `--portable-sysroot=<dir>` | Use another portable sysroot instead of the toolchain's sysroot/dotnet64-v2 |
| `--simd128` | Compile wasm_simd128.h intrinsics to Vector128 helper calls (docs/user/simd128.md) |

### Managed and native libraries

| Option | Effect |
| --- | --- |
| `--cil-reference=<assembly>` | Resolve calls to the public CExport methods of a managed library built with -shared, in the same mode |
| `--system-library=<library>` | System mode: a native shared library whose functions the program calls through generated bridges |

### NativeAOT

| Option | Effect |
| --- | --- |
| `--nativeaot` | Publish a native executable with NativeAOT (needs the .NET 10 SDK and the target's native toolchain) |
| `--runtime-id=<rid>` | NativeAOT target runtime identifier (default: this host; linux-arm64 cross-publishes from Linux x64; system mode needs linux-x64) |
| `--aot-project=<dir>` | Write the NativeAOT project into <dir> for publishing on another host (dotnet publish -r <rid>) instead of publishing here |
| `--aot-optimize=<preference>` | Balanced (default), Speed or Size |
| `--aot-instruction-set=<set>` | baseline (default: SSE2 on x64), x86-64-v3 (AVX2, BMI2, FMA: x64 CPUs since about 2015) or native (this host's CPU; the executable may not run elsewhere) |
| `--aot-debug` | Keep native debug symbols and the map file |

### Diagnostics and debugging

| Option | Effect |
| --- | --- |
| `--keep-ir=<file>` | Copy the linked bitcode, before lowering, to <file> (kept as <output>.failed.bc on a compile error) |
| `--trap-missing-arguments` | Compile a void call with fewer arguments than its definition to a trap instead of failing |
| `--diagnose-limits` | Report each function's IL size and locals, and the ones the JIT compiles without optimization |
| `--check-indirect-calls` | Check every indirect call's CIL signature against its target at run time |
| `-v` | Print the commands run and the notes for options without effect; alone, the frontend's version |

### Information

| Option | Effect |
| --- | --- |
| `--help` | This list; docs/user/driver.md is the full reference |
| `--version` | llvmnet's version, the LLVM version and the ABI tags, then the frontend's own version |
| `--driver-mode=<mode>` | Frontend to use: gcc or clang (C), g++ or clang++ (C++), flang (Fortran), as the llvmnet-clang, llvmnet-clang++ and llvmnet-flang names do |
| `-dumpmachine` | Frontend queries, answered by the selected frontend for the mode's target (x86_64-unknown-none in portable mode; also -dumpversion, -print-search-dirs, -print-resource-dir, -print-file-name=, -print-prog-name=, -print-target-triple, -print-libgcc-file-name) |

### Frontend options

Every option that is not in one of these tables is passed to Clang or Flang in order, including its value when it takes one as the next argument (the driver knows the frontends' option spellings from their own `--help`). The frontend reports unknown options and invalid values itself. Options that depend on the mode say so.

| Option | Effect |
| --- | --- |
| `-O<level>` | Optimization level. The default is -O1 without automatic vectorization (ADR-11); -O0 is accepted and -O3 fully tested |
| `-fvectorize` | Enable loop vectorization (also -fslp-vectorize); the vector IR is scalarized to CIL |
| `-std=<standard>` | Language standard; the defaults are the frontends' own (gnu17 for C, gnu++17 for C++, Fortran 2018) |
| `-g` | Debug information: compiler diagnostics get file:line:col (also -gline-tables-only); emitted assemblies carry none yet (BE-28) |
| `-D <macro>` | Preprocessor definitions, include paths and the other Clang and Flang options (-U, -I, -isystem, -include, -W..., -f..., -fopenmp, -J, -module-dir, ...) are passed through in order |
| `-pthread` | System mode: as in Clang. Portable mode: no effect, musl's pthreads are part of every program |
| `-stdlib=<library>` | System mode: as in Clang. Portable mode: no effect, the sysroot's libc++ is the C++ library |
| `-mtune=<cpu>` | System mode: as in Clang. Portable mode: no effect, portable code is scalar CIL |
| `-mllvm <option>` | LLVM options and Flang's MLIR options (-mmlir) are passed through |

### Accepted without effect

These are accepted so that build systems can pass them, and dropped. `-v` prints the reason for each one as a note.

| Option | Treatment | Reason |
| --- | --- | --- |
| `-fPIC` | no effect | position independence is a property of native code; CIL has none (also -fpic, -fPIE, -fpie, -fno-pic, -fno-PIC, -fno-pie, -fno-PIE, -pie, -no-pie, -nopie, -static-pie) |
| `-static` | portable: no effect; system: not accepted | portable: a portable program is always one statically linked image; system: a system program uses the host's shared C library and native libraries through generated bridges, which cannot be linked statically |
| `-s` | no effect | there is no native symbol table to strip; --aot-debug controls NativeAOT symbols |
| `-pipe` | no effect | the frontend writes bitcode files; there is no assembler to pipe to |
| `-fuse-ld=<linker>` | no effect | linking always works on bitcode, with LLVM's linker inside llvmnet |
| `-flto` | no effect | every link is a whole-program bitcode link already (also -flto=<mode>, -fno-lto) |
| `-static-libgcc` | no effect | compiler-rt's builtins come from the sysroot in portable mode and libgcc_s from the host in system mode (also -shared-libgcc, -static-libstdc++, -rtlib=, -unwindlib=) |
| `-z <keyword>` | no effect | ELF dynamic-section keywords (relro, now, noexecstack, ...) have no CIL equivalent |

### Not accepted

Each of these fails with the reason in the table, as `llvmnet: error: option '<option>' is not accepted: <reason>` (exit status 2).

| Option | Treatment | Reason |
| --- | --- | --- |
| `--native-import=<spec>` | not accepted | it was removed (ADR-04, SA-01); --runtime=system resolves every native function through generated bridges, and --system-library <shared-library> adds a library |
| `-rdynamic` | not accepted | a program's own symbols cannot be looked up with dlopen and dlsym; export them from a library built with -shared instead |
| `-e <symbol>` | not accepted | the entry point is always main (or the Fortran main program); llvmnet links LLVM bitcode and emits CIL, so native linker options have no equivalent |
| `-T <script>` | not accepted | linker scripts do not apply: llvmnet links LLVM bitcode and emits CIL, so native linker options have no equivalent |
| `-u <symbol>` | not accepted | forcing an archive member is not implemented; name the object on the command line or use -Wl,--whole-archive |
| `-Wa,<option>` | not accepted | there is no assembler step (also -Xassembler) |
| `-fsanitize=<check>` | not accepted | the sanitizer runtimes are not part of the sysroot or the system-mode link |
| `-fprofile-instr-generate` | not accepted | the profiling runtime is not part of the sysroot or the system-mode link (also -fprofile-instr-generate=, -fprofile-generate, -fprofile-arcs, -ftest-coverage, --coverage) |
| `-nolibc` | portable: not accepted; system: passed to the frontend | portable: the C library cannot be left out on its own (crt1.o needs it); -nodefaultlibs leaves out every sysroot library |
| `-m<option>` | portable: not accepted; system: passed to the frontend | portable: -m<option> selects a CPU, instruction set or native ABI detail, and it would change the versioned portable ABI (dotnet64-v2), whose target, data layout and headers are fixed; use --runtime=system for a native target; -mllvm, -mmlir, -mtune= and the -module... options are the exceptions |
| `--target=<triple>` | portable: not accepted; system: passed to the frontend | portable: it would change the versioned portable ABI (dotnet64-v2), whose target, data layout and headers are fixed; use --runtime=system for a native target (also -target <triple>, --sysroot, -isysroot, -fshort-wchar, -fshort-enums, -fpack-struct) |

### -Wl, and -Xlinker options

The comma-separated parts of every `-Wl,` option and each `-Xlinker` value are classified one by one. Any other linker option fails: llvmnet links LLVM bitcode and emits CIL, so native linker options have no equivalent.

| Option | Treatment | Reason |
| --- | --- | --- |
| `-Wl,--start-group` | handled | Accepted; archives are rescanned until no member resolves a symbol, as if every archive were in one group (also --end-group, -( and -)) |
| `-Wl,--whole-archive` | handled | Link every member of the archives that follow, until --no-whole-archive |
| `-Wl,--version` | handled | Print the linker interface version and exit |
| `-Wl,-rpath <dir>` | no effect | there is no native loader path to record (also -rpath=, -rpath-link, --enable-new-dtags, --disable-new-dtags) |
| `-Wl,--as-needed` | no effect | archive members are linked only when they resolve a symbol, and there are no shared-object dependencies (also --no-as-needed, -Bstatic, -Bdynamic, -Bsymbolic, -Bsymbolic-functions) |
| `-Wl,--gc-sections` | no effect | unreferenced definitions are removed from every executable anyway (also --no-gc-sections, --icf=, -O<n>, --sort-common) |
| `-Wl,-z <keyword>` | no effect | ELF dynamic-section keywords have no CIL equivalent |
| `-Wl,--build-id` | no effect | there is no ELF note section; the assembly's MVID is derived from its contents (also --build-id=, --hash-style=) |
| `-Wl,-soname <name>` | no effect | a CIL library is named by its assembly name (also -soname=, -h) |
| `-Wl,-s` | no effect | there is no native symbol table to strip (also --strip-all, -S, --strip-debug) |
| `-Wl,--no-undefined` | no effect | an unresolved symbol is always a link error (also --allow-shlib-undefined, --no-allow-shlib-undefined) |
| `-Wl,-Map <file>` | no effect | no link map is written; --diagnose-limits reports the emitted functions (also -Map=) |
| `-Wl,--version-script <file>` | no effect | a library exports its default-visibility external definitions; use -fvisibility and visibility attributes (also --version-script=, --exclude-libs) |
| `-Wl,--export-dynamic` | not accepted | as -rdynamic: a program's own symbols cannot be looked up with dlopen and dlsym (also -E) |
| `-Wl,--allow-multiple-definition` | not accepted | a duplicate definition is always an error, which names both files |
<!-- end of the generated part -->

## Link inputs

A link takes objects (`.o`, `.bc`, `.ll`), archives (`.a`, or `-l` with `-L`) and, in system mode, native libraries (`--system-library`). A file whose extension names no source language is linked by its content, as Clang hands such a file to the linker, and the linker recognizes objects and archives by their content as a native one does: an LLVM bitcode object or an archive (thin archives included) under any name, also through `-l:<file>`. CMake's Generic platform names objects `.obj`; llvmnet's own CMake platform names `SHARED` libraries, which are archives, `lib<name>.so` ([linking.md](linking.md#the-cmake-platform)), and Chromium's toolchains name archives `.so` or `.lib`. Every input must carry the ABI tag of the link's runtime mode, which `llvmnet -c` records; an object of the other mode, or untagged LLVM IR, is rejected (`llvmnet --runtime=<mode> -x ir -c` tags IR). The rules:

- **Objects** are always linked, in order. `-c` on a `.ll`, `.bc` or `.o` input produces an object for the mode (IR is compiled and tagged; an object keeps its tag, which must match).
- **Archives** are archives of bitcode objects made with `llvmnet-ar` (LLVM's `llvm-ar`). A member is linked when it defines a symbol that is referenced and still undefined; the archives are rescanned until no member resolves anything more, in command-line order within each pass. The order of archives on the command line therefore never matters, as if every archive were inside one `--start-group`/`--end-group` pair (both markers are accepted). An undefined weak reference does not extract a member, as with a native linker. When two archives define the same symbol, the member of the archive named first is the one linked, and the other is left out unless another of its symbols is needed, in which case the link fails as a duplicate. An executable's entry point counts as referenced: while nothing linked defines `main` (or a Fortran main program), a member that defines it is linked, as a native link extracts it through `crt1.o`'s reference to `main`. That is how gtest_main and Catch2's main are used; a library (`-shared`) or a `-r` link has no entry point.
- **`-Wl,--whole-archive`** (or `-Xlinker --whole-archive`) links every member of the archives that follow it, until `-Wl,--no-whole-archive`, which is how a library of constructors or plug-ins with no referenced symbol is linked in full.
- **`-l<name>`** finds `lib<name>.a` in the `-L` directories, in order; `-l:<file>` finds `<file>` by its exact name. `-lc`, `-lm`, `-lpthread`, `-ldl`, `-lrt`, `-lutil`, `-lcrypt` and `-lresolv` need no archive in either mode, nor do `-lc++`, `-lc++abi`, `-lstdc++`, `-lsupc++` and the Fortran runtime names in portable mode: those libraries are part of the runtime.
- **Duplicate definitions** are always an error, which names both files: `duplicate definition of 'f' in b.o; it is already defined in a.o`, with archive members written as `libx.a(member.o)`.
- **`-r`** links the objects and the needed archive members into one relocatable bitcode object instead of an image, without the runtime libraries; `-emit-llvm` does the same and also links the runtime libraries of the mode. A library or a `-r` link needs at least one object as its root, or a whole archive; an executable can take everything, `main` included, from archives.
- **Runtime libraries.** A portable executable links musl's start object `crt1.o` and the sysroot archives (OpenMP, flang-rt, libc++, libc++abi, musl, the compiler-rt builtins) after the inputs. `-nostartfiles` leaves out the start object (then `main` is called directly, without musl's initialization), `-nodefaultlibs` the archives (the sysroot's `lib/` archives can be named explicitly), `-nostdlib++` libc++ and libc++abi, and `-nostdlib` all of them. A portable library (`-shared`) links none of them, because musl's process state belongs to the program image. A system executable links flang-rt built against glibc; its C library is the host's.

## Calls that contradict their definition

The linker sees every function's definition, so it checks each direct call against it; [dev/calling-convention.md](../dev/calling-convention.md#direct-calls) lists the differences that x86-64's calling convention makes harmless. One shape is what autoconf, Meson, CMake and ffmpeg link to check that a function exists: a call that passes no arguments through a prototype without parameters (`char f(void);`, or `int f();` without a prototype). When such a call contradicts the definition, in its parameters or its return type, it compiles to a run-time trap instead of an error, and the link prints

```text
llvmnet: warning: emitting runtime trap: Invalid direct call through a prototype without parameters: main -> __gmtime_r: i8 () versus ptr (ptr, ptr)
```

so the probe links as it does natively. A program that executes the call stops with `InvalidProgramException` and the same message. `-w` silences the warning, as it silences the frontend's. `--trap-missing-arguments` does the same for a `void` call with fewer arguments than a `void` definition takes. Every other contradiction is a compile error (`Incompatible direct call signature`). In system mode, a call to a function of a native library goes through a bridge with the call site's signature, as a native call does, and is not checked.

## Response files

`@file` inserts the arguments read from `file`, with shell-style quoting (`"..."`, `'...'`, backslash escapes) and nested `@file` references up to 32 levels; a response file that includes itself is an error.

## Environment

| Variable | Effect |
| --- | --- |
| `LLVMNET_CLANG`, `LLVMNET_FLANG`, `LLVMNET_OPT` | The frontend and `opt` executables; otherwise the bundled `libexec/llvm/bin` tools or `clang-22`, `flang-22` and `opt-22` on `PATH` |
| `LLVMNET_DOTNET` | The `dotnet` command used for NativeAOT publishing and by launchers |
| `LLVMNET_ROOT` | The toolchain root (the directory with `sysroot/`); otherwise found from the compiler's location |
| `LLVMNET_DRIVER` | `clang`, `clang++` or `flang`, set by the alias scripts; `--driver-mode=` overrides it |
| `LLVMNET_TRACE=1` | Print a .NET stack trace with every compiler error |

## Exit status

| Status | Meaning |
| --- | --- |
| 0 | Success |
| 1 | A compilation error: an unsupported construct, an unresolved symbol, a link failure; or the frontend's or NativeAOT publishing's own failure, whose status is passed through |
| 2 | A usage error: a rejected or malformed option, a missing value, an invalid combination |
| 3 | An internal compiler error; the linked IR is kept next to the output as `<output>.failed.bc` and the message asks for a report |

Errors are printed as `file:line:col: error: ...` when the input has debug information, otherwise as `llvmnet: error: ...`, followed by notes naming the function and the LLVM instruction. Clang's and Flang's own diagnostics pass through unchanged.

## Examples

```sh
llvmnet-clang -O2 -c module.c -o module.o                  # a bitcode object
llvmnet-ar rcs libmodule.a module.o                        # an archive of them
llvmnet-clang main.c -L. -lmodule -o program.dll           # a CIL assembly
dotnet program.dll

llvmnet-clang++ -std=c++20 -fno-exceptions app.cpp -o app  # a launcher plus app.llvmnet/
llvmnet-flang -O3 solver.f90 --nativeaot -o solver         # a native executable
llvmnet-clang --runtime=system client.c --system-library ./libnative.so -o client.dll
llvmnet-clang -shared exports.c -o Exports.dll             # a managed library
llvmnet-clang consumer.c --cil-reference Exports.dll -o consumer.dll
llvmnet-clang -### -O3 main.c -o main                      # show the frontend commands
```
