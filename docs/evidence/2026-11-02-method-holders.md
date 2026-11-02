# Evidence: BE-47 programs with more than 65,535 methods (2026-11-02)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang/Flang 22.1.8, starting from commit `0f4f108`.

## How it was found

Building LLVM, Clang and Flang 22.1.8 with llvmnet in portable mode (an experiment kept outside the repository, under `artifacts/selfhost/`, like the library survey) compiled all 3,708 C++ sources and linked and translated every tool. `opt` ran, but `clang-22` stopped before its first instruction:

```
Unhandled exception. System.TypeLoadException: Type 'Program' from assembly 'program, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null' contains more methods than the current implementation allows.
   at __Startup.Main(String[])
```

Every function became a static method of the one class `Program`, and CoreCLR's class loader refuses a type with more than 65,535 methods. The link and the translation succeed, so nothing reported the problem until the program started. Method counts of the images (`LlvmNet.Checks inspect`):

| Image | Method bodies |
| --- | ---: |
| `opt` | 58,369 |
| `clang-22` | 119,681 |
| `flang-22` | 234,651 |

In system mode each address-taken function also gets a native-callable entry point (`__callback_<n>`) on `Program`, so a program with a table of 35,000 function pointers reaches the limit with half as many functions.

## Change

Internal methods (translated functions, thread-local getters, system mode's host wrappers with their P/Invoke methods, and callback entry points) are defined 32,768 per class: `Program` first, then further internal classes also named `Program`, in namespaces `LlvmNet.Methods1`, `LlvmNet.Methods2`, and so on. Static fields already overflow the same way into `__Statics<n>` classes. The methods are `internal` instead of `private`, so they can call one another across classes. Exported methods of a library, the entry methods and the type initializer stay on `Program`: every way into an image (`__Startup.Main`, a library export) goes through `Program`, so its type initializer, which allocates and initializes the globals, runs before any method of another class. Keeping the class name `Program` keeps the runtime's `abort` call stack, which lists methods of classes named `Program`, unchanged.

A program with fewer than 32,768 internal methods is translated into the same classes as before; only the visibility flags of its internal methods change, which no code generation depends on.

## Test

`tests/many-functions.sh` (tag `core`) generates C with `tests/c/many-functions.py`:
- 70,000 functions, each calling its predecessor unless its index is a multiple of 8, so calls cross every class boundary;
- a table of pointers to all of them (in system mode, 70,000 callback entry points);
- a thread-local call counter and an initialized global table;
- calls to `printf` and to `qsort` with a callback from the program's last functions.

The program must print what the native build prints, in portable and system mode. A `-shared` library with the same functions and one export is called through `--cil-reference`, so the first method entered is the export on `Program`, which must initialize the globals that the methods in the other classes read.

| | Before (`0f4f108`) | After |
| --- | --- | --- |
| Program, portable | `TypeLoadException` at start | prints the native output |
| Program, system | `TypeLoadException` at start | prints the native output |
| Library through its export, both modes | compiling the consumer fails: `--cil-reference` loads the library and gets the same `TypeLoadException` | prints the native output |

The test takes 74 s, most of it translating the four 70,000-function modules.

The first version of the change moved only translated functions, and the system-mode program still failed with the same exception: its 70,000 callback entry points were still on `Program`. The test found this, and the callbacks, host wrappers and thread-local getters now use the same classes.

## Large programs

With the change, the llvmnet-built `clang-22` (four classes) and `flang-22` (eight classes) start and work under the JIT and as NativeAOT executables. The experiment checked them against the native tools:
- all 3,542 compile commands of the LLVM, Clang, MLIR and Flang build, replayed with the built `clang-22`, give the same IR as native Clang;
- llvmnet's portable and system sysroots, rebuilt with the built clang, flang, opt and llvm-ar, have the same IR in every archive member;
- `tests/run.sh --tag core --exclude-tag slow` passes 89 of 89 with them.

These runs are outside the repository and are not reproducible from it.

## Gate

`tests/run.sh --tag core --tag toolchain --exclude-tag slow` with this change: 100 passed, 0 failed in 355 s (`many-functions` 90 s of it, run in parallel with the others).

Commands: `bash tests/many-functions.sh`; `tests/run.sh --tag core --tag toolchain --exclude-tag slow`; `dotnet test tests/LlvmNet.UnitTests -c Release` (139 passed).
